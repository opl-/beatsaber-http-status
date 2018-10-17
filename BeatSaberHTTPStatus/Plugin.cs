using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IllusionPlugin;
using UnityEngine;
using UnityEngine.SceneManagement;

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

		private MainGameSceneSetupData mainSetupData;
		private GamePauseManager gamePauseManager;
		private ScoreController scoreController;
		private GameplayManager gameplayManager;
		private AudioTimeSyncController audioTimeSyncController;
		private BeatmapObjectCallbackController beatmapObjectCallbackController;
		private PlayerHeadAndObstacleInteraction playerHeadAndObstacleInteraction;

		/// protected NoteCutInfo AfterCutScoreBuffer._noteCutInfo
		private FieldInfo noteCutInfoField = typeof(AfterCutScoreBuffer).GetField("_noteCutInfo", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// protected List<AfterCutScoreBuffer> ScoreController._afterCutScoreBuffers // contains a list of after cut buffers
		private FieldInfo afterCutScoreBuffersField = typeof(ScoreController).GetField("_afterCutScoreBuffers", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// private int AfterCutScoreBuffer#_afterCutScoreWithMultiplier
		private FieldInfo afterCutScoreWithMultiplierField = typeof(AfterCutScoreBuffer).GetField("_afterCutScoreWithMultiplier", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// private int AfterCutScoreBuffer#_multiplier
		private FieldInfo afterCutScoreBufferMultiplierField = typeof(AfterCutScoreBuffer).GetField("_multiplier", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// protected bool ScoreController._playerHeadAndObstacleInteraction
		private FieldInfo playerHeadAndObstacleInteractionField = typeof(ScoreController).GetField("_playerHeadAndObstacleInteraction", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// private static LevelCompletionResults.Rank LevelCompletionResults.GetRankForScore(int score, int maxPossibleScore)
		private MethodInfo getRankForScoreMethod = typeof(LevelCompletionResults).GetMethod("GetRankForScore", BindingFlags.NonPublic | BindingFlags.Static);
		/// private GameSongController GameplayManager._gameSongController
		private FieldInfo gameSongControllerField = typeof(GameplayManager).GetField("_gameSongController", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// private AudioTimeSyncController GameSongController._audioTimeSyncController
		private FieldInfo audioTimeSyncControllerField = typeof(GameSongController).GetField("_audioTimeSyncController", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

		public string Name {
			get {return "HTTP Status";}
		}

		public string Version {
			get {return "$VERSION$";} // Populated by MSBuild
		}

		public void OnApplicationStart() {
			if (initialized) return;
			initialized = true;

			SceneManager.sceneLoaded += OnSceneLoaded;

			server = new HTTPServer(statusManager);
			server.InitServer();
		}

		public void OnApplicationQuit() {
			SceneManager.sceneLoaded -= OnSceneLoaded;

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

		private void OnSceneLoaded(Scene newScene, LoadSceneMode mode) {
			var gameStatus = statusManager.gameStatus;

			gameStatus.scene = newScene.name;

			if (newScene.name == "Menu") {
				// Menu
				headInObstacle = false;

				// TODO: get the current song, mode and mods while in menu
				gameStatus.ResetMapInfo();

				gameStatus.ResetPerformance();

				statusManager.EmitStatusUpdate(ChangedProperties.AllButNoteCut, "menu");
			} else if (newScene.name == "StandardLevel") {
				// In game
				mainSetupData = Resources.FindObjectsOfTypeAll<MainGameSceneSetupData>().FirstOrDefault();
				if (mainSetupData == null) {
					Console.WriteLine("[HTTP Status] Couldn't find MainGameSceneSetupData");
					return;
				}

				gamePauseManager = Resources.FindObjectsOfTypeAll<GamePauseManager>().FirstOrDefault();
				if (gamePauseManager == null) {
					Console.WriteLine("[HTTP Status] Couldn't find GamePauseManager");
					return;
				}

				scoreController = Resources.FindObjectsOfTypeAll<ScoreController>().FirstOrDefault();
				if (scoreController == null) {
					Console.WriteLine("[HTTP Status] Couldn't find ScoreController");
					return;
				}

				gameplayManager = Resources.FindObjectsOfTypeAll<GameplayManager>().FirstOrDefault();
				if (gameplayManager == null) {
					Console.WriteLine("[HTTP Status] Couldn't find GameplayManager");
					return;
				}

				beatmapObjectCallbackController = Resources.FindObjectsOfTypeAll<BeatmapObjectCallbackController>().FirstOrDefault();
				if (beatmapObjectCallbackController == null) {
					Console.WriteLine("[HTTP Status] Couldn't find BeatmapObjectCallbackController");
					return;
				}

				GameSongController gameSongController = (GameSongController) gameSongControllerField.GetValue(gameplayManager);
				audioTimeSyncController = (AudioTimeSyncController) audioTimeSyncControllerField.GetValue(gameSongController);
				playerHeadAndObstacleInteraction = (PlayerHeadAndObstacleInteraction) playerHeadAndObstacleInteractionField.GetValue(scoreController);

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

				var diff = mainSetupData.difficultyLevel;
				var level = diff.level;

				gameStatus.mode = mainSetupData.gameplayMode.ToString();

				gameStatus.songName = level.songName;
				gameStatus.songSubName = level.songSubName;
				gameStatus.songAuthorName = level.songAuthorName;
				gameStatus.songBPM = level.beatsPerMinute;
				gameStatus.songTimeOffset = (long) (level.songTimeOffset * 1000f);
				gameStatus.length = (long) (level.audioClip.length * 1000f);
				gameStatus.start = GetCurrentTime();
				gameStatus.paused = 0;
				gameStatus.difficulty = diff.difficulty.Name();
				gameStatus.notesCount = diff.beatmapData.notesCount;
				gameStatus.obstaclesCount = diff.beatmapData.obstaclesCount;
				gameStatus.maxScore = ScoreController.MaxScoreForNumberOfNotes(diff.beatmapData.notesCount);

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

				// TODO: obstaclesOption can be All, FullHeightOnly or None. Reflect that?
				gameStatus.modObstacles = mainSetupData.gameplayOptions.obstaclesOption.ToString();
				gameStatus.modNoEnergy = mainSetupData.gameplayOptions.noEnergy;
				gameStatus.modMirror = mainSetupData.gameplayOptions.mirror;

				statusManager.EmitStatusUpdate(ChangedProperties.AllButNoteCut, "songStart");
			} else {
				statusManager.EmitStatusUpdate(ChangedProperties.AllButNoteCut, "scene");
			}
		}

		private void AddSubscriber(object obj, string field, Action action) {
			Type t = obj.GetType();
			FieldInfo gameEventField = t.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

			if (gameEventField == null) {
				Console.WriteLine("[HTTP Status] Can't subscribe to " + t.Name + "." + field);
				return;
			}

			MethodInfo methodInfo = gameEventField.FieldType.GetMethod("Subscribe");
			methodInfo.Invoke(gameEventField.GetValue(obj), new object[] {action});
		}

		private void RemoveSubscriber(object obj, string field, Action action) {
			Type t = obj.GetType();
			FieldInfo gameEventField = t.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

			if (gameEventField == null) {
				Console.WriteLine("[HTTP Status] Can't unsubscribe from " + t.Name + "." + field);
				return;
			}

			MethodInfo methodInfo = gameEventField.FieldType.GetMethod("Unsubscribe");
			methodInfo.Invoke(gameEventField.GetValue(obj), new object[] {action});
		}

		public void OnUpdate() {
			bool currentHeadInObstacle = playerHeadAndObstacleInteraction.intersectingObstacles.Count > 0;

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
			statusManager.gameStatus.start = GetCurrentTime() - (long) (audioTimeSyncController.songTime * 1000f);
			statusManager.gameStatus.paused = 0;

			statusManager.EmitStatusUpdate(ChangedProperties.Beatmap, "resume");
		}

		public void OnNoteWasCut(NoteData noteData, NoteCutInfo noteCutInfo, int multiplier) {
			// Event order: combo, multiplier, scoreController.noteWasCut, (LateUpdate) scoreController.scoreDidChange, afterCut, (LateUpdate) scoreController.scoreDidChange

			var gameStatus = statusManager.gameStatus;

			SetNoteCutStatus(noteData, noteCutInfo);

			int score = 0;
			int afterScore = 0;

			ScoreController.ScoreWithoutMultiplier(noteCutInfo, null, out score, out afterScore);

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
					acsb.didFinishEvent += OnNoteWasFullyCut;
					break;
				}
			}
		}

		public void OnNoteWasFullyCut(AfterCutScoreBuffer acsb) {
			int score;
			int afterScore;

			// public ScoreController.ScoreWithoutMultiplier(NoteCutInfo, SaberAfterCutSwingRatingCounter, out int beforeCutScore, out int afterCutScore)
			ScoreController.ScoreWithoutMultiplier((NoteCutInfo) noteCutInfoField.GetValue(acsb), null, out score, out afterScore);

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

			if (noteData.noteType == NoteType.Bomb) {
				statusManager.gameStatus.passedBombs++;

				statusManager.EmitStatusUpdate(ChangedProperties.Performance, "bombMissed");
			} else {
				statusManager.gameStatus.passedNotes++;
				statusManager.gameStatus.missedNotes++;

				statusManager.EmitStatusUpdate(ChangedProperties.Performance, "noteMissed");
			}
		}

		public void OnScoreDidChange(int score) {
			statusManager.gameStatus.score = score;
			statusManager.gameStatus.currentMaxScore = ScoreController.MaxScoreForNumberOfNotes(statusManager.gameStatus.passedNotes);

			// public static string LevelCompletionResults.GetRankName(LevelCompletionResults.Rank)
			LevelCompletionResults.Rank rank = (LevelCompletionResults.Rank) getRankForScoreMethod.Invoke(null, new object[] {score, statusManager.gameStatus.currentMaxScore});
			statusManager.gameStatus.rank = LevelCompletionResults.GetRankName(rank);

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
