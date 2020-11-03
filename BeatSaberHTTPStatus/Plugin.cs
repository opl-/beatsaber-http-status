using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
	internal class Plugin {
		public static Plugin instance {get; private set;}

		private StatusManager statusManager = new StatusManager();
		private HTTPServer server;

		private bool headInObstacle = false;

		private GameplayCoreSceneSetupData gameplayCoreSceneSetupData;
		private PauseController pauseController;
		private ScoreController scoreController;
		private MultiplayerSessionManager multiplayerSessionManager;
		private MultiplayerController multiplayerController;
		private MonoBehaviour gameplayManager;
		private GameplayModifiersModelSO gameplayModifiersSO;
		private GameplayModifiers gameplayModifiers;
		private AudioTimeSyncController audioTimeSyncController;
		private BeatmapObjectCallbackController beatmapObjectCallbackController;
		private PlayerHeadAndObstacleInteraction playerHeadAndObstacleInteraction;
		private GameSongController gameSongController;
		private GameEnergyCounter gameEnergyCounter;
		private Dictionary<NoteCutInfo, NoteData> noteCutMapping = new Dictionary<NoteCutInfo, NoteData>();
		/// <summary>
		/// Beat Saber 1.12.1 removes NoteData.id, forcing us to generate our own note IDs to allow users to easily link events about the same note.
		/// Before 1.12.1 the noteID matched the note order in the beatmap file, but this is impossible to replicate now without hooking into the level loading code.
		/// </summary>
		private NoteData[] noteToIdMapping = null;
		private int lastNoteId = 0;

		/// private PlayerHeadAndObstacleInteraction ScoreController._playerHeadAndObstacleInteraction;
		private FieldInfo scoreControllerHeadAndObstacleInteractionField = typeof(ScoreController).GetField("_playerHeadAndObstacleInteraction", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// protected NoteCutInfo CutScoreBuffer._noteCutInfo
		private FieldInfo noteCutInfoField = typeof(CutScoreBuffer).GetField("_noteCutInfo", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// protected List<CutScoreBuffer> ScoreController._cutScoreBuffers // contains a list of after cut buffers
		private FieldInfo afterCutScoreBuffersField = typeof(ScoreController).GetField("_cutScoreBuffers", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// private int CutScoreBuffer#_multiplier
		private FieldInfo cutScoreBufferMultiplierField = typeof(CutScoreBuffer).GetField("_multiplier", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
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
		}

		[OnExit]
		public void OnApplicationQuit() {
			SceneManager.activeSceneChanged -= OnActiveSceneChanged;

			if (pauseController != null) {
				pauseController.didPauseEvent -= OnGamePause;
				pauseController.didResumeEvent -= OnGameResume;
			}

			if (scoreController != null) {
				scoreController.noteWasCutEvent -= OnNoteWasCut;
				scoreController.noteWasMissedEvent -= OnNoteWasMissed;
				scoreController.scoreDidChangeEvent -= OnScoreDidChange;
				scoreController.comboDidChangeEvent -= OnComboDidChange;
				scoreController.multiplierDidChangeEvent -= OnMultiplierDidChange;
			}

			if (gameEnergyCounter != null) {
				gameEnergyCounter.gameEnergyDidChangeEvent -= OnEnergyDidChange;
			}

			CleanUpMultiplayer();

			if (beatmapObjectCallbackController != null) {
				beatmapObjectCallbackController.beatmapEventDidTriggerEvent -= OnBeatmapEventDidTrigger;
			}

			server.StopServer();
		}
		public void CleanUpMultiplayer() {
			if (multiplayerSessionManager != null) {
				multiplayerSessionManager.disconnectedEvent -= OnMultiplayerDisconnected;
				multiplayerSessionManager = null;
			}

			if (multiplayerController != null) {
				multiplayerController.stateChangedEvent -= OnMultiplayerStateChanged;
				multiplayerController = null;
			}
		}

		public void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
			log.Info("scene.name=" + newScene.name);
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.scene = newScene.name;

			if (newScene.name == "MenuCore") {
				// Menu
				Gamemode.Init();

				// TODO: get the current song, mode and mods while in menu
				HandleMenuStart();
			} else if (newScene.name == "GameCore") {
				// In game
				HandleSongStart();
			}
		}

		public void HandleMenuStart() {
			GameStatus gameStatus = statusManager.gameStatus;
			gameStatus.scene = multiplayerController != null ? "MultiplayerLobby" : "Menu"; // XXX: impossible because multiplayerController is always cleaned up before this

			gameStatus.ResetMapInfo();

			gameStatus.ResetPerformance();

			// Release references for AfterCutScoreBuffers that don't resolve due to player leaving the map before finishing.
			noteCutMapping.Clear();

			// Clear note id mappings.
			noteToIdMapping = null;

			statusManager.EmitStatusUpdate(ChangedProperties.AllButNoteCut, "menu");
		}

		public async void HandleSongStart() {
			GameStatus gameStatus = statusManager.gameStatus;
			log.Info("0");

			// Check for multiplayer early to abort if needed: gameplay controllers don't exist in multiplayer until later
			multiplayerSessionManager = FindFirstOrDefaultOptional<MultiplayerSessionManager>();
			multiplayerController = FindFirstOrDefaultOptional<MultiplayerController>();

			if (multiplayerSessionManager && multiplayerController) {
				Plugin.log.Info("Multiplayer Level loaded");

				MultiplayerSessionManager multiplayerSessionManager = FindFirstOrDefaultOptional<MultiplayerSessionManager>();

				// Do not do anything if we are just a spectator
				// XXX: emit multiplayer lobby
				if (multiplayerSessionManager?.isSpectating == true) return;

				// public event Action<DisconnectedReason> MultiplayerSessionManager#disconnectedEvent;
				multiplayerSessionManager.disconnectedEvent += OnMultiplayerDisconnected;
				// public event Action<State> MultiplayerController#stateChangedEvent;
				multiplayerController.stateChangedEvent += OnMultiplayerStateChanged;

				log.Info("multiplayer state = " + multiplayerController.state);

				// Do nothing until the next state change to Intro.
				if (multiplayerController.state != MultiplayerController.State.Intro) {
					return;
				}
			}

			gameStatus.scene = "Song";

			// FIXME: i should probably clean references to all this when song is over
			pauseController = FindFirstOrDefaultOptional<PauseController>();
			scoreController = FindFirstOrDefault<ScoreController>();
			gameplayManager = FindFirstOrDefaultOptional<StandardLevelGameplayManager>() as MonoBehaviour ?? FindFirstOrDefaultOptional<MissionLevelGameplayManager>();
			beatmapObjectCallbackController = FindFirstOrDefault<BeatmapObjectCallbackController>();
			gameplayModifiersSO = FindFirstOrDefault<GameplayModifiersModelSO>();
			audioTimeSyncController = FindFirstOrDefault<AudioTimeSyncController>();
			playerHeadAndObstacleInteraction = (PlayerHeadAndObstacleInteraction) scoreControllerHeadAndObstacleInteractionField.GetValue(scoreController);
			gameSongController = FindFirstOrDefault<GameSongController>();
			gameEnergyCounter = FindFirstOrDefault<GameEnergyCounter>();
			log.Info("1");

			if (multiplayerController) {
				// NOOP
			} else if (gameplayManager is StandardLevelGameplayManager) {
				Plugin.log.Info("Standard Level loaded");
			} else if (gameplayManager is MissionLevelGameplayManager) {
				Plugin.log.Info("Mission Level loaded");
			}

			gameplayCoreSceneSetupData = BS_Utils.Plugin.LevelData.GameplayCoreSceneSetupData;
			log.Info("2");
			log.Info("scoreController=" + scoreController);

			// Register event listeners
			// PauseController doesn't exist in multiplayer
			if (pauseController != null) {
				log.Info("pauseController=" + pauseController);
				// public event Action PauseController#didPauseEvent;
				pauseController.didPauseEvent += OnGamePause;
				// public event Action PauseController#didResumeEvent;
				pauseController.didResumeEvent += OnGameResume;
			}
			// public ScoreController#noteWasCutEvent<NoteData, NoteCutInfo, int multiplier> // called after AfterCutScoreBuffer is created
			scoreController.noteWasCutEvent += OnNoteWasCut;
			// public ScoreController#noteWasMissedEvent<NoteData, int multiplier>
			scoreController.noteWasMissedEvent += OnNoteWasMissed;
			// public ScoreController#scoreDidChangeEvent<int, int> // score
			scoreController.scoreDidChangeEvent += OnScoreDidChange;
			// public ScoreController#comboDidChangeEvent<int> // combo
			scoreController.comboDidChangeEvent += OnComboDidChange;
			// public ScoreController#multiplierDidChangeEvent<int, float> // multiplier, progress [0..1]
			scoreController.multiplierDidChangeEvent += OnMultiplierDidChange;
			// public GameEnergyCounter#gameEnergyDidChangeEvent<float> // energy
			gameEnergyCounter.gameEnergyDidChangeEvent += OnEnergyDidChange;
			log.Info("2.5");
			// public event Action<BeatmapEventData> BeatmapObjectCallbackController#beatmapEventDidTriggerEvent
			beatmapObjectCallbackController.beatmapEventDidTriggerEvent += OnBeatmapEventDidTrigger;
			// public event Action GameSongController#songDidFinishEvent;
			gameSongController.songDidFinishEvent += OnLevelFinished;
			// public event Action GameEnergyCounter#gameEnergyDidReach0Event;
			gameEnergyCounter.gameEnergyDidReach0Event += OnLevelFailed;
			log.Info("3");

			IDifficultyBeatmap diff = gameplayCoreSceneSetupData.difficultyBeatmap;
			IBeatmapLevel level = diff.level;

			gameStatus.partyMode = Gamemode.IsPartyActive;
			gameStatus.mode = Gamemode.GameMode;

			gameplayModifiers = gameplayCoreSceneSetupData.gameplayModifiers;
			PlayerSpecificSettings playerSettings = gameplayCoreSceneSetupData.playerSpecificSettings;
			PracticeSettings practiceSettings = gameplayCoreSceneSetupData.practiceSettings;

			float songSpeedMul = gameplayModifiers.songSpeedMul;
			if (practiceSettings != null) songSpeedMul = practiceSettings.songSpeedMul;
			float modifierMultiplier = gameplayModifiersSO.GetTotalMultiplier(gameplayModifiers);
			log.Info("4");

			// Generate NoteData to id mappings for backwards compatiblity with <1.12.1
			noteToIdMapping = new NoteData[diff.beatmapData.cuttableNotesType + diff.beatmapData.bombsCount];
			lastNoteId = 0;
			log.Info("4.1");

			int beatmapObjectId = 0;
			var beatmapObjectsData = diff.beatmapData.beatmapObjectsData;
			log.Info("4.2");

			foreach (BeatmapObjectData beatmapObjectData in beatmapObjectsData) {
				if (beatmapObjectData is NoteData noteData) {
					noteToIdMapping[beatmapObjectId++] = noteData;
				}
			}
			log.Info("5");

			gameStatus.songName = level.songName;
			gameStatus.songSubName = level.songSubName;
			gameStatus.songAuthorName = level.songAuthorName;
			gameStatus.levelAuthorName = level.levelAuthorName;
			gameStatus.songBPM = level.beatsPerMinute;
			gameStatus.noteJumpSpeed = diff.noteJumpMovementSpeed;
			// 13 is "custom_level_" and 40 is the magic number for the length of the SHA-1 hash
			gameStatus.songHash = level.levelID.StartsWith("custom_level_") && !level.levelID.EndsWith(" WIP") ? level.levelID.Substring(13, 40) : null;
			gameStatus.levelId = level.levelID;
			gameStatus.songTimeOffset = (long) (level.songTimeOffset * 1000f / songSpeedMul);
			gameStatus.length = (long) (level.beatmapLevelData.audioClip.length * 1000f / songSpeedMul);
			gameStatus.start = GetCurrentTime() - (long) (audioTimeSyncController.songTime * 1000f / songSpeedMul);
			if (practiceSettings != null) gameStatus.start -= (long) (practiceSettings.startSongTime * 1000f / songSpeedMul);
			gameStatus.paused = 0;
			gameStatus.difficulty = diff.difficulty.Name();
			gameStatus.notesCount = diff.beatmapData.cuttableNotesType;
			gameStatus.bombsCount = diff.beatmapData.bombsCount;
			gameStatus.obstaclesCount = diff.beatmapData.obstaclesCount;
			gameStatus.environmentName = level.environmentInfo.sceneInfo.sceneName;

			gameStatus.maxScore = gameplayModifiersSO.MaxModifiedScoreForMaxRawScore(ScoreModel.MaxRawScoreForNumberOfNotes(diff.beatmapData.cuttableNotesType), gameplayModifiers, gameplayModifiersSO);
			gameStatus.maxRank = RankModelHelper.MaxRankForGameplayModifiers(gameplayModifiers, gameplayModifiersSO).ToString();
			log.Info("6");

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
			log.Info("7");

			gameStatus.ResetPerformance();

			gameStatus.modifierMultiplier = modifierMultiplier;
			gameStatus.songSpeedMultiplier = songSpeedMul;
			gameStatus.batteryLives = gameEnergyCounter.batteryLives;

			gameStatus.modObstacles = gameplayModifiers.enabledObstacleType.ToString();
			gameStatus.modInstaFail = gameplayModifiers.instaFail;
			gameStatus.modNoFail = gameplayModifiers.noFail;
			gameStatus.modBatteryEnergy = gameplayModifiers.energyType == GameplayModifiers.EnergyType.Battery;
			gameStatus.modDisappearingArrows = gameplayModifiers.disappearingArrows;
			gameStatus.modNoBombs = gameplayModifiers.noBombs;
			gameStatus.modSongSpeed = gameplayModifiers.songSpeed.ToString();
			gameStatus.modNoArrows = gameplayModifiers.noArrows;
			gameStatus.modGhostNotes = gameplayModifiers.ghostNotes;
			gameStatus.modFailOnSaberClash = gameplayModifiers.failOnSaberClash;
			gameStatus.modStrictAngles = gameplayModifiers.strictAngles;
			gameStatus.modFastNotes = gameplayModifiers.fastNotes;

			gameStatus.staticLights = playerSettings.staticLights;
			gameStatus.leftHanded = playerSettings.leftHanded;
			gameStatus.playerHeight = playerSettings.playerHeight;
			gameStatus.sfxVolume = playerSettings.sfxVolume;
			gameStatus.reduceDebris = playerSettings.reduceDebris;
			gameStatus.noHUD = playerSettings.noTextsAndHuds;
			gameStatus.advancedHUD = playerSettings.advancedHud;
			gameStatus.autoRestart = playerSettings.autoRestart;
			log.Info("8");

			statusManager.EmitStatusUpdate(ChangedProperties.AllButNoteCut, "songStart");
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

		public void OnUpdate() {
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

		public void OnMultiplayerStateChanged(MultiplayerController.State state) {
			log.Info("multiplayer state = " + state);

			if (state == MultiplayerController.State.Intro) {
				// Gameplay controllers don't exist on the inisial load of GameCore, s owe need to delay it until later
				// XXX: check that this isn't fired too late
				HandleSongStart();
			}
		}

		public void OnMultiplayerDisconnected(DisconnectedReason reason) {
			CleanUpMultiplayer();

			// XXX: this should only be fired if we go from multiplayer lobby to menu and there's no scene transition because of it. gotta prevent duplicates too
			// HandleMenuStart();
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

		public void OnNoteWasCut(NoteData noteData, NoteCutInfo noteCutInfo, int multiplier) {
			// Event order: combo, multiplier, scoreController.noteWasCut, (LateUpdate) scoreController.scoreDidChange, afterCut, (LateUpdate) scoreController.scoreDidChange

			var gameStatus = statusManager.gameStatus;

			SetNoteCutStatus(noteData, noteCutInfo, true);

			int beforeCutScore = 0;
			int afterCutScore = 0;
			int cutDistanceScore = 0;

			ScoreModel.RawScoreWithoutMultiplier(noteCutInfo, out beforeCutScore, out afterCutScore, out cutDistanceScore);

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

				if (noteCutInfo.allIsOK) {
					gameStatus.hitNotes++;

					statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteCut");
				} else {
					gameStatus.missedNotes++;

					statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteMissed");
				}
			}

			List<CutScoreBuffer> list = (List<CutScoreBuffer>) afterCutScoreBuffersField.GetValue(scoreController);

			foreach (CutScoreBuffer acsb in list) {
				if (noteCutInfoField.GetValue(acsb) == noteCutInfo) {
					// public CutScoreBuffer#didFinishEvent<CutScoreBuffer>
					noteCutMapping.Add(noteCutInfo, noteData);

					acsb.didFinishEvent += OnNoteWasFullyCut;
					break;
				}
			}
		}

		public void OnNoteWasFullyCut(CutScoreBuffer acsb) {
			int beforeCutScore;
			int afterCutScore;
			int cutDistanceScore;

			NoteCutInfo noteCutInfo = (NoteCutInfo) noteCutInfoField.GetValue(acsb);
			NoteData noteData = noteCutMapping[noteCutInfo];

			noteCutMapping.Remove(noteCutInfo);

			SetNoteCutStatus(noteData, noteCutInfo, false);

			// public static ScoreModel.RawScoreWithoutMultiplier(NoteCutInfo, out int beforeCutRawScore, out int afterCutRawScore, out int cutDistanceRawScore)
			ScoreModel.RawScoreWithoutMultiplier(noteCutInfo, out beforeCutScore, out afterCutScore, out cutDistanceScore);

			int multiplier = (int) cutScoreBufferMultiplierField.GetValue(acsb);

			statusManager.gameStatus.initialScore = beforeCutScore + cutDistanceScore;
			statusManager.gameStatus.finalScore = beforeCutScore + afterCutScore + cutDistanceScore;
			statusManager.gameStatus.cutDistanceScore = cutDistanceScore;
			statusManager.gameStatus.cutMultiplier = multiplier;

			statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteFullyCut");

			acsb.didFinishEvent -= OnNoteWasFullyCut;
		}

		private void SetNoteCutStatus(NoteData noteData, NoteCutInfo noteCutInfo = null, bool initialCut = true) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.ResetNoteCut();

			// Backwards compatibility for <1.12.1
			gameStatus.noteID = -1;
			// Check the near notes first for performance
			for (int i = Math.Max(0, lastNoteId - 10); i < noteToIdMapping.Length; i++) {
				if (NoteDataEquals(noteToIdMapping[i], noteData)) {
					gameStatus.noteID = i;
					if (i > lastNoteId) lastNoteId = i;
					break;
				}
			}
			// If that failed, check the rest of the notes in reverse order
			if (gameStatus.noteID == -1) {
				for (int i = Math.Max(0, lastNoteId - 11); i >= 0; i--) {
					if (NoteDataEquals(noteToIdMapping[i], noteData)) {
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

			if (noteCutInfo != null) {
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
		}

		public void OnNoteWasMissed(NoteData noteData, int multiplier) {
			// Event order: combo, multiplier, scoreController.noteWasMissed, (LateUpdate) scoreController.scoreDidChange

			statusManager.gameStatus.batteryEnergy = gameEnergyCounter.batteryEnergy;

			SetNoteCutStatus(noteData);

			if (noteData.colorType == ColorType.None) {
				statusManager.gameStatus.passedBombs++;

				statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "bombMissed");
			} else {
				statusManager.gameStatus.passedNotes++;
				statusManager.gameStatus.missedNotes++;

				statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteMissed");
			}
		}

		public void OnScoreDidChange(int scoreBeforeMultiplier, int scoreAfterMultiplier) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.score = scoreAfterMultiplier;

			int currentMaxScoreBeforeMultiplier = ScoreModel.MaxRawScoreForNumberOfNotes(gameStatus.passedNotes);
			gameStatus.currentMaxScore = gameplayModifiersSO.MaxModifiedScoreForMaxRawScore(currentMaxScoreBeforeMultiplier, gameplayModifiers, gameplayModifiersSO);

			RankModel.Rank rank = RankModel.GetRankForScore(scoreBeforeMultiplier, gameStatus.score, currentMaxScoreBeforeMultiplier, gameStatus.currentMaxScore);
			gameStatus.rank = RankModel.GetRankName(rank);

			statusManager.EmitStatusUpdate(ChangedProperties.Performance, "scoreChanged");
		}

		public void OnComboDidChange(int combo) {
			statusManager.gameStatus.combo = combo;
			// public int ScoreController#maxCombo
			statusManager.gameStatus.maxCombo = scoreController.maxCombo;
		}

		public void OnEnergyDidChange(float energy) {
			statusManager.gameStatus.energy = energy;
			statusManager.EmitStatusUpdate(ChangedProperties.Performance, "energyChanged");
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

		public void OnBeatmapEventDidTrigger(BeatmapEventData beatmapEventData) {
			statusManager.gameStatus.beatmapEventType = (int) beatmapEventData.type;
			statusManager.gameStatus.beatmapEventValue = beatmapEventData.value;

			statusManager.EmitStatusUpdate(ChangedProperties.BeatmapEvent, "beatmapEvent");
		}

		public static long GetCurrentTime() {
			return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}

		public static bool NoteDataEquals(NoteData a, NoteData b) {
			return a.time == b.time && a.lineIndex == b.lineIndex && a.noteLineLayer == b.noteLineLayer && a.colorType == b.colorType && a.cutDirection == b.cutDirection && a.duration == b.duration;
		}

		public class PluginTickerScript : PersistentSingleton<PluginTickerScript> {
			public void Update() {
				if (Plugin.instance != null) Plugin.instance.OnUpdate();
			}
		}
	}
}
