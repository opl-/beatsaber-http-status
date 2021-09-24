using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using BS_Utils.Gameplay;
using IPA;
using IPALogger = IPA.Logging.Logger;

// Interesting props and methods:
// protected const int ScoreController.kMaxCutScore // 110
// public BeatmapObjectSpawnController.noteWasCutEvent<BeatmapObjectSpawnController, NoteController, NoteCutInfo> // Listened to by scoreManager for its cut event and therefore is raised before combo, multiplier and score changes
// public BeatmapObjectSpawnController.noteWasMissedEvent<BeatmapObjectSpawnController, NoteController> // Same as above, but for misses
// public BeatmapObjectSpawnController.obstacleDidPassAvoidedMarkEvent<BeatmapObjectSpawnController, ObstacleController>
// public int ScoreController.prevFrameScore
// protected ScoreController._baseScore

namespace BeatSaberHTTPStatus {
	[Plugin(RuntimeOptions.SingleStartInit)]
	internal class Plugin : ICutScoreBufferDidFinishEvent {
		public static Plugin instance {get; private set;}

		private StatusManager statusManager = new StatusManager();
		private HTTPServer server;

		private bool headInObstacle = false;

		private GameplayCoreSceneSetupData gameplayCoreSceneSetupData;
		private PauseController pauseController;
		private ScoreController scoreController;
		private MultiplayerSessionManager multiplayerSessionManager;
		private MultiplayerController multiplayerController;
		private MultiplayerLocalActivePlayerFacade multiplayerLocalActivePlayerFacade;
		private MonoBehaviour gameplayManager;
		private GameplayModifiersModelSO gameplayModifiersSO;
		private GameplayModifiers gameplayModifiers;
		private List<GameplayModifierParamsSO> gameplayModiferParamsList;
		private AudioTimeSyncController audioTimeSyncController;
		private BeatmapObjectCallbackController beatmapObjectCallbackController;
		private PlayerHeadAndObstacleInteraction playerHeadAndObstacleInteraction;
		private GameSongController gameSongController;
		private GameEnergyCounter gameEnergyCounter;
		private Dictionary<CutScoreBuffer, NoteFullyCutData> noteCutMapping = new Dictionary<CutScoreBuffer, NoteFullyCutData>();
		/// <summary>
		/// Beat Saber 1.12.1 removes NoteData.id, forcing us to generate our own note IDs to allow users to easily link events about the same note.
		/// Before 1.12.1 the noteID matched the note order in the beatmap file, but this is impossible to replicate now without hooking into the level loading code.
		/// </summary>
		private NoteData[] noteToIdMapping = null;
		private int lastNoteId = 0;
		/// <summary>
		/// This is used to delay the `HandleSongStart` call by a single frame when not in multiplayer, to ensure that all resources exist.<br/>
		/// This isn't necessary in multiplayer since Zenject (which I'm currently regretting not using) has time to do its thing there before our code runs. Yeah, I hate this.
		/// </summary>
		private bool doDelayedSongStart = false;

		/// private PlayerHeadAndObstacleInteraction ScoreController._playerHeadAndObstacleInteraction;
		private FieldInfo scoreControllerHeadAndObstacleInteractionField = typeof(ScoreController).GetField("_playerHeadAndObstacleInteraction", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// protected readonly LazyCopyHashSet<ISaberSwingRatingCounterDidFinishReceiver> SaberSwingRatingCounter._didFinishReceivers = new LazyCopyHashSet<ISaberSwingRatingCounterDidFinishReceiver>() // contains the desired CutScoreBuffer
		private FieldInfo saberSwingRatingCounterDidFinishReceiversField = typeof(SaberSwingRatingCounter).GetField("_didFinishReceivers", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// private static LevelCompletionResults.Rank LevelCompletionResults.GetRankForScore(int score, int maxPossibleScore)
		private MethodInfo getRankForScoreMethod = typeof(LevelCompletionResults).GetMethod("GetRankForScore", BindingFlags.NonPublic | BindingFlags.Static);

		public static readonly string PluginVersion = "$SEMVER_VERSION$"; // Populated by MSBuild
		public static readonly string GameVersion = "$BS_VERSION$"; // Populated by MSBuild

		public string Name {
			get {return "HTTP Status";}
		}

		public string Version {
			get {return PluginVersion;}
		}

		public static IPALogger log;

		[Init]
		public void Init(IPALogger logger) {
			log = logger;
		}

		[OnStart]
		public void OnApplicationStart() {
			if (instance != null) return;
			instance = this;

			PluginTickerScript.TouchInstance();

			server = new HTTPServer(statusManager);
			server.InitServer();
			
			SceneManager.activeSceneChanged += OnActiveSceneChanged;

			Gamemode.Init();
		}

		[OnExit]
		public void OnApplicationQuit() {
			SceneManager.activeSceneChanged -= OnActiveSceneChanged;

			CleanUpSong();

			CleanUpMultiplayer();

			server.StopServer();
		}

		private void CleanUpSong() {
			statusManager.gameStatus.ResetMapInfo();
			statusManager.gameStatus.ResetPerformance();

			// Release references for AfterCutScoreBuffers that don't resolve due to player leaving the map before finishing.
			noteCutMapping.Clear();

			// Clear note id mappings.
			noteToIdMapping = null;

			if (pauseController != null) {
				pauseController.didPauseEvent -= OnGamePause;
				pauseController.didResumeEvent -= OnGameResume;
				pauseController = null;
			}

			if (scoreController != null) {
				scoreController.noteWasCutEvent -= OnNoteWasCut;
				scoreController.noteWasMissedEvent -= OnNoteWasMissed;
				scoreController.scoreDidChangeEvent -= OnScoreDidChange;
				scoreController.comboDidChangeEvent -= OnComboDidChange;
				scoreController.multiplierDidChangeEvent -= OnMultiplierDidChange;
				scoreController = null;
			}

			if (gameplayManager != null) {
				if (gameplayManager is ILevelEndActions levelEndActions) {
					// event Action levelFailedEvent;
					levelEndActions.levelFailedEvent -= OnLevelFailed;
				}
				gameplayManager = null;
			}

			if (beatmapObjectCallbackController != null) {
				beatmapObjectCallbackController.beatmapEventDidTriggerEvent -= OnBeatmapEventDidTrigger;
				beatmapObjectCallbackController = null;
			}

			if (gameplayModifiersSO != null) {
				gameplayModifiersSO = null;
			}

			if (audioTimeSyncController != null) {
				audioTimeSyncController = null;
			}

			if (playerHeadAndObstacleInteraction != null) {
				playerHeadAndObstacleInteraction = null;
			}

			if (gameSongController != null) {
				gameSongController.songDidFinishEvent -= OnLevelFinished;
				gameSongController = null;
			}

			if (gameEnergyCounter != null) {
				gameEnergyCounter.gameEnergyDidReach0Event -= OnEnergyDidReach0Event;
				gameEnergyCounter = null;
			}

			if (gameplayCoreSceneSetupData != null) {
				gameplayCoreSceneSetupData = null;
			}

			if (gameplayModifiers != null) {
				gameplayModifiers = null;
			}

			if (gameplayModiferParamsList != null) {
				gameplayModiferParamsList = null;
			}
		}

		private void CleanUpMultiplayer() {
			if (multiplayerSessionManager != null) {
				multiplayerSessionManager.disconnectedEvent -= OnMultiplayerDisconnected;
				multiplayerSessionManager = null;
			}

			if (multiplayerController != null) {
				multiplayerController.stateChangedEvent -= OnMultiplayerStateChanged;
				multiplayerController = null;
			}

			if (multiplayerLocalActivePlayerFacade != null) {
				multiplayerLocalActivePlayerFacade.playerDidFinishEvent -= OnMultiplayerLevelFinished;
				multiplayerLocalActivePlayerFacade = null;
			}
		}

		public void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.scene = newScene.name;

			if (oldScene.name == "GameCore") {
				CleanUpSong();
			}

			if (newScene.name == "MainMenu") {
				// Menu
				// TODO: get the current song, mode and mods while in menu
				HandleMenuStart();
			} else if (newScene.name == "GameCore") {
				// In game
				HandleSongStart();
			}
		}

		public void HandleMenuStart() {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.scene = "Menu";

			statusManager.EmitStatusUpdate(ChangedProperties.AllButNoteCut, "menu");
		}

		public async void HandleSongStart() {
			GameStatus gameStatus = statusManager.gameStatus;

			// Check for multiplayer early to abort if needed: gameplay controllers don't exist in multiplayer until later
			multiplayerSessionManager = FindFirstOrDefaultOptional<MultiplayerSessionManager>();
			multiplayerController = FindFirstOrDefaultOptional<MultiplayerController>();

			if (multiplayerSessionManager && multiplayerController) {
				Plugin.log.Debug("Multiplayer Level loaded");

				// public event Action<DisconnectedReason> MultiplayerSessionManager#disconnectedEvent;
				multiplayerSessionManager.disconnectedEvent += OnMultiplayerDisconnected;

				// public event Action<State> MultiplayerController#stateChangedEvent;
				multiplayerController.stateChangedEvent += OnMultiplayerStateChanged;

				// Do nothing until the next state change to Gameplay.
				if (multiplayerController.state != MultiplayerController.State.Gameplay) {
					return;
				}

				multiplayerLocalActivePlayerFacade = FindFirstOrDefaultOptional<MultiplayerLocalActivePlayerFacade>();

				if (multiplayerLocalActivePlayerFacade != null) {
					multiplayerLocalActivePlayerFacade.playerDidFinishEvent += OnMultiplayerLevelFinished;
				}
			} else if (!doDelayedSongStart) {
				doDelayedSongStart = true;

				return;
			}

			// `wants_to_play_next_level` is set for players who don't want to play the song aka want to spectate aka are not "active". `isSpectating` is apparently not spectating.
			gameStatus.scene = multiplayerSessionManager.isSpectating || !multiplayerSessionManager.LocalPlayerHasState(NetworkConstants.wantsToPlayNextLevel) ? "Spectator" : "Song";
			gameStatus.multiplayer = multiplayerSessionManager.isConnectingOrConnected;

			pauseController = FindFirstOrDefaultOptional<PauseController>();
			scoreController = FindWithMultiplayerFix<ScoreController>();
			gameplayManager = FindFirstOrDefaultOptional<StandardLevelGameplayManager>() as MonoBehaviour ?? FindFirstOrDefaultOptional<MissionLevelGameplayManager>();
			beatmapObjectCallbackController = FindWithMultiplayerFix<BeatmapObjectCallbackController>();
			gameplayModifiersSO = FindFirstOrDefault<GameplayModifiersModelSO>();
			audioTimeSyncController = FindWithMultiplayerFix<AudioTimeSyncController>();
			playerHeadAndObstacleInteraction = (PlayerHeadAndObstacleInteraction) scoreControllerHeadAndObstacleInteractionField.GetValue(scoreController);
			gameSongController = FindWithMultiplayerFix<GameSongController>();
			gameEnergyCounter = FindWithMultiplayerFix<GameEnergyCounter>();

			if (multiplayerController) {
				// NOOP
			} else if (gameplayManager is StandardLevelGameplayManager) {
				Plugin.log.Debug("Standard Level loaded");
			} else if (gameplayManager is MissionLevelGameplayManager) {
				Plugin.log.Debug("Mission Level loaded");
			}

			gameplayCoreSceneSetupData = BS_Utils.Plugin.LevelData.GameplayCoreSceneSetupData;

			// Register event listeners
			// PauseController doesn't exist in multiplayer
			if (pauseController != null) {
				// public event Action PauseController#didPauseEvent;
				pauseController.didPauseEvent += OnGamePause;
				// public event Action PauseController#didResumeEvent;
				pauseController.didResumeEvent += OnGameResume;
			}
			// public ScoreController#noteWasCutEvent<NoteData, NoteCutInfo, int multiplier> // called after CutScoreBuffer is created
			scoreController.noteWasCutEvent += OnNoteWasCut;
			// public ScoreController#noteWasMissedEvent<NoteData, int multiplier>
			scoreController.noteWasMissedEvent += OnNoteWasMissed;
			// public ScoreController#scoreDidChangeEvent<int, int> // score
			scoreController.scoreDidChangeEvent += OnScoreDidChange;
			// public ScoreController#comboDidChangeEvent<int> // combo
			scoreController.comboDidChangeEvent += OnComboDidChange;
			// public ScoreController#multiplierDidChangeEvent<int, float> // multiplier, progress [0..1]
			scoreController.multiplierDidChangeEvent += OnMultiplierDidChange;
			// public event Action<BeatmapEventData> BeatmapObjectCallbackController#beatmapEventDidTriggerEvent
			beatmapObjectCallbackController.beatmapEventDidTriggerEvent += OnBeatmapEventDidTrigger;
			// public event Action GameSongController#songDidFinishEvent;
			gameSongController.songDidFinishEvent += OnLevelFinished;
			// public event Action GameEnergyCounter#gameEnergyDidReach0Event;
			gameEnergyCounter.gameEnergyDidReach0Event += OnEnergyDidReach0Event;
			if (gameplayManager is ILevelEndActions levelEndActions) {
				// event Action levelFailedEvent;
				levelEndActions.levelFailedEvent += OnLevelFailed;
			}

			IDifficultyBeatmap diff = gameplayCoreSceneSetupData.difficultyBeatmap;
			IBeatmapLevel level = diff.level;

			gameStatus.partyMode = Gamemode.IsPartyActive;
			gameStatus.mode = BS_Utils.Plugin.LevelData.GameplayCoreSceneSetupData.difficultyBeatmap.parentDifficultyBeatmapSet.beatmapCharacteristic.serializedName;

			gameplayModifiers = gameplayCoreSceneSetupData.gameplayModifiers;
			gameplayModiferParamsList = gameplayModifiersSO.CreateModifierParamsList(gameplayModifiers);

			PlayerSpecificSettings playerSettings = gameplayCoreSceneSetupData.playerSpecificSettings;
			PracticeSettings practiceSettings = gameplayCoreSceneSetupData.practiceSettings;

			float songSpeedMul = gameplayModifiers.songSpeedMul;
			if (practiceSettings != null) songSpeedMul = practiceSettings.songSpeedMul;

			int beatmapObjectId = 0;
			var beatmapObjectsData = diff.beatmapData.beatmapObjectsData;

			// Generate NoteData to id mappings for backwards compatiblity with <1.12.1
			noteToIdMapping = new NoteData[beatmapObjectsData.Count(obj => obj is NoteData)];
			lastNoteId = 0;

			foreach (BeatmapObjectData beatmapObjectData in beatmapObjectsData) {
				if (beatmapObjectData is NoteData noteData) {
					noteToIdMapping[beatmapObjectId++] = noteData;
				}
			}

			gameStatus.songName = level.songName;
			gameStatus.songSubName = level.songSubName;
			gameStatus.songAuthorName = level.songAuthorName;
			gameStatus.levelAuthorName = level.levelAuthorName;
			gameStatus.songBPM = level.beatsPerMinute;
			gameStatus.noteJumpSpeed = diff.noteJumpMovementSpeed;
			// 13 is "custom_level_" and 40 is the magic number for the length of the SHA-1 hash
			gameStatus.songHash = Regex.IsMatch(level.levelID, "^custom_level_[0-9A-F]{40}", RegexOptions.IgnoreCase) && !level.levelID.EndsWith(" WIP") ? level.levelID.Substring(13, 40) : null;
			gameStatus.levelId = level.levelID;
			gameStatus.songTimeOffset = (long) (level.songTimeOffset * 1000f / songSpeedMul);
			gameStatus.length = (long) (level.beatmapLevelData.audioClip.length * 1000f / songSpeedMul);
			gameStatus.start = GetCurrentTime() - (long) (audioTimeSyncController.songTime * 1000f / songSpeedMul);
			if (practiceSettings != null) gameStatus.start -= (long) (practiceSettings.startSongTime * 1000f / songSpeedMul);
			gameStatus.paused = 0;
			gameStatus.difficulty = diff.difficulty.Name();
			gameStatus.difficultyEnum = Enum.GetName(typeof(BeatmapDifficulty), diff.difficulty);
			gameStatus.notesCount = diff.beatmapData.cuttableNotesCount;
			gameStatus.bombsCount = diff.beatmapData.bombsCount;
			gameStatus.obstaclesCount = diff.beatmapData.obstaclesCount;
			gameStatus.environmentName = level.environmentInfo.sceneInfo.sceneName;

			try {
				// From https://support.unity3d.com/hc/en-us/articles/206486626-How-can-I-get-pixels-from-unreadable-textures-
				var texture = (await level.GetCoverImageAsync(CancellationToken.None)).texture;
				var active = RenderTexture.active;
				var temporary = RenderTexture.GetTemporary(
					texture.width,
					texture.height,
					0,
					RenderTextureFormat.Default,
					RenderTextureReadWrite.Linear
				);

				Graphics.Blit(texture, temporary);
				RenderTexture.active = temporary;

				var cover = new Texture2D(texture.width, texture.height);
				cover.ReadPixels(new Rect(0, 0, temporary.width, temporary.height), 0, 0);
				cover.Apply();

				RenderTexture.active = active;
				RenderTexture.ReleaseTemporary(temporary);

				gameStatus.songCover = System.Convert.ToBase64String(
					ImageConversion.EncodeToPNG(cover)
				);
			} catch {
				gameStatus.songCover = null;
			}

			gameStatus.ResetPerformance();

			UpdateModMultiplier();

			gameStatus.songSpeedMultiplier = songSpeedMul;
			gameStatus.batteryLives = gameEnergyCounter.batteryLives;

			gameStatus.modObstacles = gameplayModifiers.enabledObstacleType.ToString();
			gameStatus.modInstaFail = gameplayModifiers.instaFail;
			gameStatus.modNoFail = gameplayModifiers.noFailOn0Energy;
			gameStatus.modBatteryEnergy = gameplayModifiers.energyType == GameplayModifiers.EnergyType.Battery;
			gameStatus.modDisappearingArrows = gameplayModifiers.disappearingArrows;
			gameStatus.modNoBombs = gameplayModifiers.noBombs;
			gameStatus.modSongSpeed = gameplayModifiers.songSpeed.ToString();
			gameStatus.modNoArrows = gameplayModifiers.noArrows;
			gameStatus.modGhostNotes = gameplayModifiers.ghostNotes;
			gameStatus.modFailOnSaberClash = gameplayModifiers.failOnSaberClash;
			gameStatus.modStrictAngles = gameplayModifiers.strictAngles;
			gameStatus.modFastNotes = gameplayModifiers.fastNotes;
			gameStatus.modSmallNotes = gameplayModifiers.smallCubes;
			gameStatus.modProMode = gameplayModifiers.proMode;
			gameStatus.modZenMode = gameplayModifiers.zenMode;

			var environmentEffectsFilterPreset = diff.difficulty == BeatmapDifficulty.ExpertPlus ? playerSettings.environmentEffectsFilterExpertPlusPreset : playerSettings.environmentEffectsFilterDefaultPreset;
			// Backwards compatibility for <1.13.4
			gameStatus.staticLights = environmentEffectsFilterPreset != EnvironmentEffectsFilterPreset.AllEffects;
			gameStatus.leftHanded = playerSettings.leftHanded;
			gameStatus.playerHeight = playerSettings.playerHeight;
			gameStatus.sfxVolume = playerSettings.sfxVolume;
			gameStatus.reduceDebris = playerSettings.reduceDebris;
			gameStatus.noHUD = playerSettings.noTextsAndHuds;
			gameStatus.advancedHUD = playerSettings.advancedHud;
			gameStatus.autoRestart = playerSettings.autoRestart;
			gameStatus.saberTrailIntensity = playerSettings.saberTrailIntensity;
			gameStatus.environmentEffects = environmentEffectsFilterPreset.ToString();
			gameStatus.hideNoteSpawningEffect = playerSettings.hideNoteSpawnEffect;

			statusManager.EmitStatusUpdate(ChangedProperties.AllButNoteCut, "songStart");
		}

		/// <summary>
		/// Workaround for controller types created for multiplayer lingering around after leaving, resulting in those getting returned after switching back to singleplayer. See #74.
		/// </summary>
		private T FindWithMultiplayerFix<T>() where T: UnityEngine.Object {
			return multiplayerSessionManager.isConnectingOrConnected ? FindFirstOrDefault<T>() : FindLastOrDefault<T>();
		}

		/// <summary>
		/// Workaround for controller types created for multiplayer lingering around after leaving, resulting in those getting returned after switching back to singleplayer. See #74.
		/// </summary>
		private T FindOptionalWithMultiplayerFix<T>() where T: UnityEngine.Object {
			return multiplayerSessionManager.isConnectingOrConnected ? FindFirstOrDefaultOptional<T>() : FindLastOrDefaultOptional<T>();
		}

		private static T FindFirstOrDefault<T>() where T: UnityEngine.Object {
			T obj = Resources.FindObjectsOfTypeAll<T>().FirstOrDefault();
			if (obj == null) {
				Plugin.log.Error("Couldn't find " + typeof(T).FullName);
				throw new InvalidOperationException("Couldn't find " + typeof(T).FullName);
			}
			return obj;
		}

		private static T FindFirstOrDefaultOptional<T>() where T: UnityEngine.Object {
			T obj = Resources.FindObjectsOfTypeAll<T>().FirstOrDefault();
			return obj;
		}

		private static T FindLastOrDefault<T>() where T: UnityEngine.Object {
			T obj = Resources.FindObjectsOfTypeAll<T>().LastOrDefault();
			if (obj == null) {
				Plugin.log.Error("Couldn't find " + typeof(T).FullName);
				throw new InvalidOperationException("Couldn't find " + typeof(T).FullName);
			}
			return obj;
		}

		private static T FindLastOrDefaultOptional<T>() where T: UnityEngine.Object {
			T obj = Resources.FindObjectsOfTypeAll<T>().LastOrDefault();
			return obj;
		}

		public void UpdateModMultiplier() {
			GameStatus gameStatus = statusManager.gameStatus;

			float energy = gameEnergyCounter.energy;

			gameStatus.modifierMultiplier = gameplayModifiersSO.GetTotalMultiplier(gameplayModiferParamsList, energy);

			gameStatus.maxScore = gameplayModifiersSO.MaxModifiedScoreForMaxRawScore(ScoreModel.MaxRawScoreForNumberOfNotes(gameplayCoreSceneSetupData.difficultyBeatmap.beatmapData.cuttableNotesCount), gameplayModiferParamsList, gameplayModifiersSO, energy);
			gameStatus.maxRank = RankModelHelper.MaxRankForGameplayModifiers(gameplayModifiers, gameplayModifiersSO, energy).ToString();
		}

		public void OnUpdate() {
			if (doDelayedSongStart) {
				HandleSongStart();

				// Reset the variable after calling the method so we don't get stuck in a loop.
				doDelayedSongStart = false;
			}

			bool currentHeadInObstacle = false;

			if (playerHeadAndObstacleInteraction != null) {
				currentHeadInObstacle = playerHeadAndObstacleInteraction.intersectingObstacles.Count > 0;
			}

			if (!headInObstacle && currentHeadInObstacle) {
				headInObstacle = true;

				statusManager.EmitStatusUpdate(ChangedProperties.Performance, "obstacleEnter");
			} else if (headInObstacle && !currentHeadInObstacle) {
				headInObstacle = false;

				statusManager.EmitStatusUpdate(ChangedProperties.Performance, "obstacleExit");
			}
		}

		public void OnMultiplayerDisconnected(DisconnectedReason reason) {
			CleanUpMultiplayer();
		}

		public void OnMultiplayerStateChanged(MultiplayerController.State state) {
			if (state == MultiplayerController.State.Gameplay) {
				// Gameplay controllers don't exist on the initial load of GameCore, so we need to delay it until later.
				// Additionally, waiting until Gameplay means we don't need to hook into the multiplayer audio sync controllers.
				HandleSongStart();
			}
		}

		public void OnGamePause() {
			statusManager.gameStatus.paused = GetCurrentTime();

			statusManager.EmitStatusUpdate(ChangedProperties.Beatmap, "pause");
		}

		public void OnGameResume() {
			statusManager.gameStatus.start = GetCurrentTime() - (long) (audioTimeSyncController.songTime * 1000f / statusManager.gameStatus.songSpeedMultiplier);
			statusManager.gameStatus.paused = 0;

			statusManager.EmitStatusUpdate(ChangedProperties.Beatmap, "resume");
		}

		public void OnNoteWasCut(NoteData noteData, in NoteCutInfo noteCutInfo, int multiplier) {
			// Event order: combo, multiplier, scoreController.noteWasCut, (LateUpdate) scoreController.scoreDidChange, afterCut, (LateUpdate) scoreController.scoreDidChange

			var gameStatus = statusManager.gameStatus;

			SetNoteDataStatus(noteData);
			SetNoteCutStatus(noteCutInfo, true);

			int beforeCutScore = 0;
			int afterCutScore = 0;
			int cutDistanceScore = 0;

			// public static void ScoreModel.RawScoreWithoutMultiplier(ISaberSwingRatingCounter saberSwingRatingCounter, float cutDistanceToCenter, out int beforeCutRawScore, out int afterCutRawScore, out int cutDistanceRawScore)
			ScoreModel.RawScoreWithoutMultiplier(noteCutInfo.swingRatingCounter, noteCutInfo.cutDistanceToCenter, out beforeCutScore, out afterCutScore, out cutDistanceScore);

			gameStatus.initialScore = beforeCutScore + cutDistanceScore;
			gameStatus.finalScore = -1;
			gameStatus.cutDistanceScore = cutDistanceScore;
			gameStatus.cutMultiplier = multiplier;

			if (noteData.colorType == ColorType.None) {
				gameStatus.passedBombs++;
				gameStatus.hitBombs++;

				statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "bombCut");
			} else {
				gameStatus.passedNotes++;

				UpdateCurrentMaxScore();

				if (noteCutInfo.allIsOK) {
					gameStatus.hitNotes++;

					statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteCut");
				} else {
					gameStatus.missedNotes++;

					statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteMissed");
				}
			}

			// Bombs don't have a SwingRatingCounter
			if (noteCutInfo.swingRatingCounter != null) {
				var didFinishReceivers = (LazyCopyHashSet<ISaberSwingRatingCounterDidFinishReceiver>) saberSwingRatingCounterDidFinishReceiversField.GetValue(noteCutInfo.swingRatingCounter);
				foreach (ISaberSwingRatingCounterDidFinishReceiver receiver in didFinishReceivers.items) {
					if (receiver is CutScoreBuffer csb) {
						noteCutMapping.Add(csb, new NoteFullyCutData(noteData, noteCutInfo));

						// public ILazyCopyHashSet<ICutScoreBufferDidFinishEvent> CutScoreBuffer#didFinishEvent
						csb.didFinishEvent.Add(this);
						break;
					}
				}
			}
		}

		public void HandleCutScoreBufferDidFinish(CutScoreBuffer csb) {
			OnNoteWasFullyCut(csb);
		}

		public void OnNoteWasFullyCut(CutScoreBuffer csb) {
			int beforeCutScore;
			int afterCutScore;
			int cutDistanceScore;
			
			NoteFullyCutData noteFullyCutData = noteCutMapping[csb];
			noteCutMapping.Remove(csb);

			NoteCutInfo noteCutInfo = noteFullyCutData.noteCutInfo;

			SetNoteDataStatus(noteFullyCutData.noteData);
			SetNoteCutStatus(noteCutInfo, false);

			// public static void ScoreModel.RawScoreWithoutMultiplier(ISaberSwingRatingCounter saberSwingRatingCounter, float cutDistanceToCenter, out int beforeCutRawScore, out int afterCutRawScore, out int cutDistanceRawScore)
			ScoreModel.RawScoreWithoutMultiplier(noteCutInfo.swingRatingCounter, noteCutInfo.cutDistanceToCenter, out beforeCutScore, out afterCutScore, out cutDistanceScore);

			statusManager.gameStatus.initialScore = beforeCutScore + cutDistanceScore;
			statusManager.gameStatus.finalScore = beforeCutScore + afterCutScore + cutDistanceScore;
			statusManager.gameStatus.cutDistanceScore = cutDistanceScore;
			statusManager.gameStatus.cutMultiplier = csb.multiplier;

			statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteFullyCut");

			csb.didFinishEvent.Remove(this);
		}

		private void SetNoteDataStatus(NoteData noteData) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.ResetNoteCut();

			// Backwards compatibility for <1.12.1
			gameStatus.noteID = -1;
			// Check the near notes first for performance
			for (int i = Math.Max(0, lastNoteId - 10); i < noteToIdMapping.Length; i++) {
				if (NoteDataEquals(noteToIdMapping[i], noteData, gameStatus.modNoArrows)) {
					gameStatus.noteID = i;
					if (i > lastNoteId) lastNoteId = i;
					break;
				}
			}
			// If that failed, check the rest of the notes in reverse order
			if (gameStatus.noteID == -1) {
				for (int i = Math.Max(0, lastNoteId - 11); i >= 0; i--) {
					if (NoteDataEquals(noteToIdMapping[i], noteData, gameStatus.modNoArrows)) {
						gameStatus.noteID = i;
						break;
					}
				}
			}

			// Backwards compatibility for <1.12.1
			gameStatus.noteType = noteData.colorType == ColorType.None ? "Bomb" : noteData.colorType == ColorType.ColorA ? "NoteA" : noteData.colorType == ColorType.ColorB ? "NoteB" : noteData.colorType.ToString();
			gameStatus.noteCutDirection = noteData.cutDirection.ToString();
			gameStatus.noteLine = noteData.lineIndex;
			gameStatus.noteLayer = (int) noteData.noteLineLayer;
			// If long notes are ever introduced, this name will make no sense
			gameStatus.timeToNextBasicNote = noteData.timeToNextColorNote;
		}

		/// <summary>
		/// Sets note cut related status data. Should be called after SetNoteDataStatus.
		/// </summary>
		private void SetNoteCutStatus(NoteCutInfo noteCutInfo, bool initialCut = true) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.speedOK = noteCutInfo.speedOK;
			gameStatus.directionOK = noteCutInfo.directionOK;
			gameStatus.saberTypeOK = noteCutInfo.saberTypeOK;
			gameStatus.wasCutTooSoon = noteCutInfo.wasCutTooSoon;
			gameStatus.saberSpeed = noteCutInfo.saberSpeed;
			gameStatus.saberDirX = noteCutInfo.saberDir[0];
			gameStatus.saberDirY = noteCutInfo.saberDir[1];
			gameStatus.saberDirZ = noteCutInfo.saberDir[2];
			gameStatus.saberType = noteCutInfo.saberType.ToString();
			gameStatus.swingRating = noteCutInfo.swingRatingCounter == null ? -1 : initialCut ? noteCutInfo.swingRatingCounter.beforeCutRating : noteCutInfo.swingRatingCounter.afterCutRating;
			gameStatus.timeDeviation = noteCutInfo.timeDeviation;
			gameStatus.cutDirectionDeviation = noteCutInfo.cutDirDeviation;
			gameStatus.cutPointX = noteCutInfo.cutPoint[0];
			gameStatus.cutPointY = noteCutInfo.cutPoint[1];
			gameStatus.cutPointZ = noteCutInfo.cutPoint[2];
			gameStatus.cutNormalX = noteCutInfo.cutNormal[0];
			gameStatus.cutNormalY = noteCutInfo.cutNormal[1];
			gameStatus.cutNormalZ = noteCutInfo.cutNormal[2];
			gameStatus.cutDistanceToCenter = noteCutInfo.cutDistanceToCenter;
		}

		public void OnNoteWasMissed(NoteData noteData, int multiplier) {
			// Event order: combo, multiplier, scoreController.noteWasMissed, (LateUpdate) scoreController.scoreDidChange

			statusManager.gameStatus.batteryEnergy = gameEnergyCounter.batteryEnergy;

			SetNoteDataStatus(noteData);

			if (noteData.colorType == ColorType.None) {
				statusManager.gameStatus.passedBombs++;

				statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "bombMissed");
			} else {
				statusManager.gameStatus.passedNotes++;
				statusManager.gameStatus.missedNotes++;

				UpdateCurrentMaxScore();

				statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteMissed");
			}
		}

		public void OnScoreDidChange(int scoreBeforeMultiplier, int scoreAfterMultiplier) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.rawScore = scoreBeforeMultiplier;
			gameStatus.score = scoreAfterMultiplier;

			UpdateCurrentMaxScore();

			statusManager.EmitStatusUpdate(ChangedProperties.Performance, "scoreChanged");
		}

		public void UpdateCurrentMaxScore() {
			GameStatus gameStatus = statusManager.gameStatus;

			int currentMaxScoreBeforeMultiplier = ScoreModel.MaxRawScoreForNumberOfNotes(gameStatus.passedNotes);
			gameStatus.currentMaxScore = gameplayModifiersSO.MaxModifiedScoreForMaxRawScore(currentMaxScoreBeforeMultiplier, gameplayModiferParamsList, gameplayModifiersSO, gameEnergyCounter.energy);

			RankModel.Rank rank = RankModel.GetRankForScore(gameStatus.rawScore, gameStatus.score, currentMaxScoreBeforeMultiplier, gameStatus.currentMaxScore);
			gameStatus.rank = RankModel.GetRankName(rank);
		}

		public void OnComboDidChange(int combo) {
			statusManager.gameStatus.combo = combo;
			// public int ScoreController#maxCombo
			statusManager.gameStatus.maxCombo = scoreController.maxCombo;
		}

		public void OnMultiplierDidChange(int multiplier, float multiplierProgress) {
			statusManager.gameStatus.multiplier = multiplier;
			statusManager.gameStatus.multiplierProgress = multiplierProgress;
		}

		public void OnLevelFinished() {
			statusManager.EmitStatusUpdate(ChangedProperties.Performance, "finished");
		}

		public void OnLevelFailed() {
			statusManager.EmitStatusUpdate(ChangedProperties.Performance, "failed");
		}

		public void OnMultiplayerLevelFinished(MultiplayerLevelCompletionResults results) {
			if (results.levelEndState == MultiplayerLevelCompletionResults.MultiplayerLevelEndState.Cleared) {
				OnLevelFinished();
			} else if (results.levelEndState == MultiplayerLevelCompletionResults.MultiplayerLevelEndState.Failed) {
				OnLevelFailed();
			}
		}

		public void OnEnergyDidReach0Event() {
			if (statusManager.gameStatus.modNoFail) {
				statusManager.gameStatus.softFailed = true;

				UpdateModMultiplier();
				UpdateCurrentMaxScore();

				statusManager.EmitStatusUpdate(ChangedProperties.BeatmapAndPerformanceAndMod, "softFailed");
			}
		}

		public void OnBeatmapEventDidTrigger(BeatmapEventData beatmapEventData) {
			statusManager.gameStatus.beatmapEventType = (int) beatmapEventData.type;
			statusManager.gameStatus.beatmapEventValue = beatmapEventData.value;

			statusManager.EmitStatusUpdate(ChangedProperties.BeatmapEvent, "beatmapEvent");
		}

		public static long GetCurrentTime() {
			return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}

		public static bool NoteDataEquals(NoteData a, NoteData b, bool noArrows = false) {
			return a.time == b.time && a.lineIndex == b.lineIndex && a.noteLineLayer == b.noteLineLayer && a.colorType == b.colorType && (noArrows || a.cutDirection == b.cutDirection) && a.duration == b.duration;
		}

		public class PluginTickerScript : PersistentSingleton<PluginTickerScript> {
			public void Update() {
				if (Plugin.instance != null) Plugin.instance.OnUpdate();
			}
		}

		/// <summary>
		/// Stores data necessary to emit the noteFullyCut event.
		/// </summary>
		private readonly struct NoteFullyCutData {
			public readonly NoteData noteData;

			public readonly NoteCutInfo noteCutInfo;

			public NoteFullyCutData(NoteData noteData, NoteCutInfo noteCutInfo) {
				this.noteData = noteData;
				this.noteCutInfo = noteCutInfo;
			}
		}
	}
}
