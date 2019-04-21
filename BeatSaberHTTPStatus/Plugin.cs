using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IllusionPlugin;
using UnityEngine;
using UnityEngine.SceneManagement;
using BS_Utils.Gameplay;

// Interesting props and methods:
// protected const int ScoreController.kMaxCutScore // 110
// public BeatmapObjectSpawnController.noteWasCutEvent<BeatmapObjectSpawnController, NoteController, NoteCutInfo> // Listened to by scoreManager for its cut event and therefore is raised before combo, multiplier and score changes
// public BeatmapObjectSpawnController.noteWasMissedEvent<BeatmapObjectSpawnController, NoteController> // Same as above, but for misses
// public BeatmapObjectSpawnController.obstacleDidPassAvoidedMarkEvent<BeatmapObjectSpawnController, ObstacleController>
// public int ScoreController.prevFrameScore
// protected ScoreController._baseScore

namespace BeatSaberHTTPStatus {
	public class Plugin : IPlugin {
		private bool initialized;

		private StatusManager statusManager = new StatusManager();
		private HTTPServer server;

		private bool headInObstacle = false;

		private GameplayCoreSceneSetupData gameplayCoreSceneSetupData;
		private GamePauseManager gamePauseManager;
		private ScoreController scoreController;
		private StandardLevelGameplayManager gameplayManager;
		private GameplayModifiersModelSO gameplayModifiersSO;
		private AudioTimeSyncController audioTimeSyncController;
		private BeatmapObjectCallbackController beatmapObjectCallbackController;
		private PlayerHeadAndObstacleInteraction playerHeadAndObstacleInteraction;
		private GameEnergyCounter gameEnergyCounter;
		private Dictionary<NoteCutInfo, NoteData> noteCutMapping = new Dictionary<NoteCutInfo, NoteData>();

		/// protected NoteCutInfo AfterCutScoreBuffer._noteCutInfo
		private FieldInfo noteCutInfoField = typeof(AfterCutScoreBuffer).GetField("_noteCutInfo", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// protected List<AfterCutScoreBuffer> ScoreController._afterCutScoreBuffers // contains a list of after cut buffers
		private FieldInfo afterCutScoreBuffersField = typeof(ScoreController).GetField("_afterCutScoreBuffers", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// private int AfterCutScoreBuffer#_afterCutScoreWithMultiplier
		private FieldInfo afterCutScoreWithMultiplierField = typeof(AfterCutScoreBuffer).GetField("_afterCutScoreWithMultiplier", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// private int AfterCutScoreBuffer#_multiplier
		private FieldInfo afterCutScoreBufferMultiplierField = typeof(AfterCutScoreBuffer).GetField("_multiplier", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
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

		public static void PluginLog(string str) {
			Console.WriteLine("[HTTP Status " + PluginVersion + "] " + str);
		}

		public void OnApplicationStart() {
			if (initialized) return;
			initialized = true;

			SceneManager.activeSceneChanged += this.OnActiveSceneChanged;

			server = new HTTPServer(statusManager);
			server.InitServer();
		}

		public void OnApplicationQuit() {
			SceneManager.activeSceneChanged -= this.OnActiveSceneChanged;

			if (gamePauseManager != null) {
				RemoveSubscriber(gamePauseManager, "_gameDidPauseSignal", OnGamePause);
				RemoveSubscriber(gamePauseManager, "_gameDidResumeSignal", OnGameResume);
			}

			if (scoreController != null) {
				scoreController.noteWasCutEvent += OnNoteWasCut;
				scoreController.noteWasMissedEvent -= OnNoteWasMissed;
				scoreController.scoreDidChangeEvent -= OnScoreDidChange;
				scoreController.comboDidChangeEvent -= OnComboDidChange;
				scoreController.multiplierDidChangeEvent -= OnMultiplierDidChange;
			}

			if (gameplayManager != null) {
				RemoveSubscriber(gameplayManager, "_levelFinishedSignal", OnLevelFinished);
				RemoveSubscriber(gameplayManager, "_levelFailedSignal", OnLevelFailed);
			}

			if (beatmapObjectCallbackController != null) {
				beatmapObjectCallbackController.beatmapEventDidTriggerEvent -= OnBeatmapEventDidTrigger;
			}

			server.StopServer();
		}

		private void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.scene = newScene.name;

			if (newScene.name == "MenuCore") {
				// Menu
				gameStatus.scene = "Menu";

				Gamemode.Init();

				// TODO: get the current song, mode and mods while in menu
				gameStatus.ResetMapInfo();

				gameStatus.ResetPerformance();

				// Release references for AfterCutScoreBuffers that don't resolve due to player leaving the map before finishing.
				noteCutMapping.Clear();

				statusManager.EmitStatusUpdate(ChangedProperties.AllButNoteCut, "menu");
			} else if (newScene.name == "GameCore") {
				// In game
				gameStatus.scene = "Song";

				gamePauseManager = FindFirstOrDefault<GamePauseManager>();
				scoreController = FindFirstOrDefault<ScoreController>();
				gameplayManager = FindFirstOrDefault<StandardLevelGameplayManager>();
				beatmapObjectCallbackController = FindFirstOrDefault<BeatmapObjectCallbackController>();
				gameplayModifiersSO = FindFirstOrDefault<GameplayModifiersModelSO>();
				audioTimeSyncController = FindFirstOrDefault<AudioTimeSyncController>();
				playerHeadAndObstacleInteraction = FindFirstOrDefault<PlayerHeadAndObstacleInteraction>();
				gameEnergyCounter = FindFirstOrDefault<GameEnergyCounter>();

				gameplayCoreSceneSetupData = BS_Utils.Plugin.LevelData.GameplayCoreSceneSetupData;

				// Register event listeners
				// private GameEvent GamePauseManager#_gameDidPauseSignal
				AddSubscriber(gamePauseManager, "_gameDidPauseSignal", OnGamePause);
				// private GameEvent GamePauseManager#_gameDidResumeSignal
				AddSubscriber(gamePauseManager, "_gameDidResumeSignal", OnGameResume);
				// public ScoreController#noteWasCutEvent<NoteData, NoteCutInfo, int multiplier> // called after AfterCutScoreBuffer is created
				scoreController.noteWasCutEvent += OnNoteWasCut;
				// public ScoreController#noteWasMissedEvent<NoteData, int multiplier>
				scoreController.noteWasMissedEvent += OnNoteWasMissed;
				// public ScoreController#scoreDidChangeEvent<int> // score
				scoreController.scoreDidChangeEvent += OnScoreDidChange;
				// public ScoreController#comboDidChangeEvent<int> // combo
				scoreController.comboDidChangeEvent += OnComboDidChange;
				// public ScoreController#multiplierDidChangeEvent<int, float> // multiplier, progress [0..1]
				scoreController.multiplierDidChangeEvent += OnMultiplierDidChange;
				// private GameEvent GameplayManager#_levelFinishedSignal
				AddSubscriber(gameplayManager, "_levelFinishedSignal", OnLevelFinished);
				// private GameEvent GameplayManager#_levelFailedSignal
				AddSubscriber(gameplayManager, "_levelFailedSignal", OnLevelFailed);
				// public event Action<BeatmapEventData> BeatmapObjectCallbackController#beatmapEventDidTriggerEvent
				beatmapObjectCallbackController.beatmapEventDidTriggerEvent += OnBeatmapEventDidTrigger;

				IDifficultyBeatmap diff = gameplayCoreSceneSetupData.difficultyBeatmap;
				IBeatmapLevel level = diff.level;

				gameStatus.partyMode = Gamemode.IsPartyActive;
				gameStatus.mode = Gamemode.GameMode;

				GameplayModifiers gameplayModifiers = gameplayCoreSceneSetupData.gameplayModifiers;
				PlayerSpecificSettings playerSettings = gameplayCoreSceneSetupData.playerSpecificSettings;
				PracticeSettings practiceSettings = gameplayCoreSceneSetupData.practiceSettings;

				float songSpeedMul = gameplayModifiers.songSpeedMul;
				if (practiceSettings != null) songSpeedMul = practiceSettings.songSpeedMul;
				float modifierMultiplier = gameplayModifiersSO.GetTotalMultiplier(gameplayModifiers);

				gameStatus.songName = level.songName;
				gameStatus.songSubName = level.songSubName;
				gameStatus.songAuthorName = level.songAuthorName;
				gameStatus.levelAuthorName = level.levelAuthorName;
				gameStatus.songBPM = level.beatsPerMinute;
				gameStatus.noteJumpSpeed = diff.noteJumpMovementSpeed;
				gameStatus.songHash = level.levelID.Substring(0, Math.Min(32, level.levelID.Length));
				gameStatus.songTimeOffset = (long) (level.songTimeOffset * 1000f / songSpeedMul);
				gameStatus.length = (long) (level.beatmapLevelData.audioClip.length * 1000f / songSpeedMul);
				gameStatus.start = GetCurrentTime() - (long) (audioTimeSyncController.songTime * 1000f / songSpeedMul);
				if (practiceSettings != null) gameStatus.start -= (long) (practiceSettings.startSongTime * 1000f / songSpeedMul);
				gameStatus.paused = 0;
				gameStatus.difficulty = diff.difficulty.Name();
				gameStatus.notesCount = diff.beatmapData.notesCount;
				gameStatus.bombsCount = diff.beatmapData.bombsCount;
				gameStatus.obstaclesCount = diff.beatmapData.obstaclesCount;
				gameStatus.environmentName = level.environmentSceneInfo.sceneName;
				gameStatus.maxScore = ScoreController.GetScoreForGameplayModifiersScoreMultiplier(ScoreController.MaxScoreForNumberOfNotes(diff.beatmapData.notesCount), modifierMultiplier);
				gameStatus.maxRank = RankModel.MaxRankForGameplayModifiers(gameplayModifiers, gameplayModifiersSO).ToString();

				try {
					// From https://support.unity3d.com/hc/en-us/articles/206486626-How-can-I-get-pixels-from-unreadable-textures-
					var texture = level.coverImage.texture;
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

				gameStatus.modifierMultiplier = modifierMultiplier;
				gameStatus.songSpeedMultiplier = songSpeedMul;
				gameStatus.batteryLives = gameEnergyCounter.batteryLives;

				gameStatus.modObstacles = gameplayModifiers.enabledObstacleType.ToString();
				gameStatus.modInstaFail = gameplayModifiers.instaFail;
				gameStatus.modNoFail = gameplayModifiers.noFail;
				gameStatus.modBatteryEnergy = gameplayModifiers.batteryEnergy;
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
				gameStatus.swapColors = playerSettings.swapColors;
				gameStatus.playerHeight = playerSettings.playerHeight;
				gameStatus.disableSFX = playerSettings.disableSFX;
				gameStatus.noHUD = playerSettings.noTextsAndHuds;
				gameStatus.advancedHUD = playerSettings.advancedHud;

				statusManager.EmitStatusUpdate(ChangedProperties.AllButNoteCut, "songStart");
			}
		}

		private static T FindFirstOrDefault<T>() where T: UnityEngine.Object {
			T obj = Resources.FindObjectsOfTypeAll<T>().FirstOrDefault();
			if (obj == null) {
				PluginLog("Couldn't find " + typeof(T).FullName);
				throw new InvalidOperationException("Couldn't find " + typeof(T).FullName);
			}
			return obj;
		}

		private void AddSubscriber(object obj, string field, Action action) {
			Type t = obj.GetType();
			FieldInfo gameEventField = t.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

			if (gameEventField == null) {
				PluginLog("Can't subscribe to " + t.Name + "." + field);
				return;
			}

			MethodInfo methodInfo = gameEventField.FieldType.GetMethod("Subscribe");
			methodInfo.Invoke(gameEventField.GetValue(obj), new object[] {action});
		}

		private void RemoveSubscriber(object obj, string field, Action action) {
			Type t = obj.GetType();
			FieldInfo gameEventField = t.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

			if (gameEventField == null) {
				PluginLog("Can't unsubscribe from " + t.Name + "." + field);
				return;
			}

			MethodInfo methodInfo = gameEventField.FieldType.GetMethod("Unsubscribe");
			methodInfo.Invoke(gameEventField.GetValue(obj), new object[] {action});
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

			SetNoteCutStatus(noteData, noteCutInfo);

			int score = 0;
			int afterScore = 0;
			int cutDistanceScore = 0;

			ScoreController.ScoreWithoutMultiplier(noteCutInfo, null, out score, out afterScore, out cutDistanceScore);

			gameStatus.initialScore = score;
			gameStatus.finalScore = -1;
			gameStatus.cutMultiplier = multiplier;

			if (noteData.noteType == NoteType.Bomb) {
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

			List<AfterCutScoreBuffer> list = (List<AfterCutScoreBuffer>) afterCutScoreBuffersField.GetValue(scoreController);

			foreach (AfterCutScoreBuffer acsb in list) {
				if (noteCutInfoField.GetValue(acsb) == noteCutInfo) {
					// public AfterCutScoreBuffer#didFinishEvent<AfterCutScoreBuffer>
					noteCutMapping.Add(noteCutInfo, noteData);

					acsb.didFinishEvent += OnNoteWasFullyCut;
					break;
				}
			}
		}

		public void OnNoteWasFullyCut(AfterCutScoreBuffer acsb) {
			int score;
			int afterScore;
			int cutDistanceScore;

			NoteCutInfo noteCutInfo = (NoteCutInfo) noteCutInfoField.GetValue(acsb);
			NoteData noteData = noteCutMapping[noteCutInfo];

			noteCutMapping.Remove(noteCutInfo);

			SetNoteCutStatus(noteData, noteCutInfo);

			// public ScoreController.ScoreWithoutMultiplier(NoteCutInfo, SaberAfterCutSwingRatingCounter, out int beforeCutScore, out int afterCutScore, out int cutDistanceScore)
			ScoreController.ScoreWithoutMultiplier(noteCutInfo, null, out score, out afterScore, out cutDistanceScore);

			int multiplier = (int) afterCutScoreBufferMultiplierField.GetValue(acsb);

			afterScore = (int) afterCutScoreWithMultiplierField.GetValue(acsb) / multiplier;

			statusManager.gameStatus.initialScore = score;
			statusManager.gameStatus.finalScore = score + afterScore;
			statusManager.gameStatus.cutMultiplier = multiplier;

			statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteFullyCut");

			acsb.didFinishEvent -= OnNoteWasFullyCut;
		}

		private void SetNoteCutStatus(NoteData noteData, NoteCutInfo noteCutInfo) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.noteID = noteData.id;
			gameStatus.noteType = noteData.noteType.ToString();
			gameStatus.noteCutDirection = noteData.cutDirection.ToString();
			gameStatus.noteLine = noteData.lineIndex;
			gameStatus.noteLayer = (int) noteData.noteLineLayer;
            gameStatus.timeToNextBasicNote = noteData.timeToNextBasicNote;
            gameStatus.speedOK = noteCutInfo.speedOK;
			gameStatus.directionOK = noteCutInfo.directionOK;
			gameStatus.saberTypeOK = noteCutInfo.saberTypeOK;
			gameStatus.wasCutTooSoon = noteCutInfo.wasCutTooSoon;
			gameStatus.saberSpeed = noteCutInfo.saberSpeed;
			gameStatus.saberDirX = noteCutInfo.saberDir[0];
			gameStatus.saberDirY = noteCutInfo.saberDir[1];
			gameStatus.saberDirZ = noteCutInfo.saberDir[2];
			gameStatus.saberType = noteCutInfo.saberType.ToString();
			gameStatus.swingRating = noteCutInfo.swingRating;
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

			if (noteData.noteType == NoteType.Bomb) {
				statusManager.gameStatus.passedBombs++;

				statusManager.EmitStatusUpdate(ChangedProperties.Performance, "bombMissed");
			} else {
				statusManager.gameStatus.passedNotes++;
				statusManager.gameStatus.missedNotes++;

				statusManager.EmitStatusUpdate(ChangedProperties.Performance, "noteMissed");
			}
		}

		public void OnScoreDidChange(int scoreBeforeMultiplier) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.score = ScoreController.GetScoreForGameplayModifiersScoreMultiplier(scoreBeforeMultiplier, gameStatus.modifierMultiplier);

			int currentMaxScoreBeforeMultiplier = ScoreController.MaxScoreForNumberOfNotes(gameStatus.passedNotes);
			gameStatus.currentMaxScore = ScoreController.GetScoreForGameplayModifiersScoreMultiplier(currentMaxScoreBeforeMultiplier, gameStatus.modifierMultiplier);

			RankModel.Rank rank = RankModel.GetRankForScore(scoreBeforeMultiplier, gameStatus.score, currentMaxScoreBeforeMultiplier, gameStatus.currentMaxScore);
			gameStatus.rank = RankModel.GetRankName(rank);

			statusManager.EmitStatusUpdate(ChangedProperties.Performance, "scoreChanged");
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

		public void OnBeatmapEventDidTrigger(BeatmapEventData beatmapEventData) {
			statusManager.gameStatus.beatmapEventType = (int) beatmapEventData.type;
			statusManager.gameStatus.beatmapEventValue = beatmapEventData.value;

			statusManager.EmitStatusUpdate(ChangedProperties.BeatmapEvent, "beatmapEvent");
		}

		public static long GetCurrentTime() {
			return (long) (DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).Ticks / TimeSpan.TicksPerMillisecond);
		}

		public void OnLevelWasLoaded(int level) {}
		public void OnLevelWasInitialized(int level) {}
		public void OnFixedUpdate() {}
	}
}
