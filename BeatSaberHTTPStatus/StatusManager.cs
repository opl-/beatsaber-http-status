using System;
using UnityEngine;
using SimpleJSON;

namespace BeatSaberHTTPStatus {
	public class StatusManager {
		public GameStatus gameStatus = new GameStatus();

		private JSONObject _statusJSON;
		public JSONObject statusJSON {
			get {return _statusJSON;}
		}

		private JSONObject _noteCutJSON;
		public JSONObject noteCutJSON {
			get {return _noteCutJSON;}
		}

		private JSONObject _beatmapEventJSON;
		public JSONObject beatmapEventJSON {
			get {return _beatmapEventJSON;}
		}

		public event Action<StatusManager, ChangedProperties, string> statusChange;

		public StatusManager() {
			_statusJSON = new JSONObject();
			_noteCutJSON = new JSONObject();
			_beatmapEventJSON = new JSONObject();

			UpdateAll();
		}

		public void EmitStatusUpdate(ChangedProperties changedProps, string cause) {
			gameStatus.updateCause = cause;

			if (changedProps.game) UpdateGameJSON();
			if (changedProps.beatmap) UpdateBeatmapJSON();
			if (changedProps.performance) UpdatePerformanceJSON();
			if (changedProps.noteCut) UpdateNoteCutJSON();
			if (changedProps.mod) {
				UpdateModJSON();
				UpdatePlayerSettingsJSON();
			}
			if (changedProps.beatmapEvent) UpdateBeatmapEventJSON();

			if (statusChange != null) statusChange(this, changedProps, cause);
		}

		private void UpdateAll() {
			UpdateGameJSON();
			UpdateBeatmapJSON();
			UpdatePerformanceJSON();
			UpdateNoteCutJSON();
			UpdateModJSON();
			UpdatePlayerSettingsJSON();
			UpdateBeatmapEventJSON();
		}

		private void UpdateGameJSON() {
			if (_statusJSON["game"] == null) _statusJSON["game"] = new JSONObject();
			JSONObject gameJSON = (JSONObject) _statusJSON["game"];

			gameJSON["pluginVersion"] = BeatSaberHTTPStatus.Plugin.PluginVersion;
			gameJSON["gameVersion"] = BeatSaberHTTPStatus.Plugin.GameVersion;
			gameJSON["scene"] = stringOrNull(gameStatus.scene);
			gameJSON["mode"] = stringOrNull(gameStatus.mode == null ? null : (gameStatus.partyMode ? "Party" : "Solo") + gameStatus.mode);
		}

		private void UpdateBeatmapJSON() {
			if (gameStatus.songName == null) {
				_statusJSON["beatmap"] = null;
				return;
			}

			if (_statusJSON["beatmap"] == null) _statusJSON["beatmap"] = new JSONObject();
			JSONObject beatmapJSON = (JSONObject) _statusJSON["beatmap"];

			beatmapJSON["songName"] = stringOrNull(gameStatus.songName);
			beatmapJSON["songSubName"] = stringOrNull(gameStatus.songSubName);
			beatmapJSON["songAuthorName"] = stringOrNull(gameStatus.songAuthorName);
			beatmapJSON["levelAuthorName"] = stringOrNull(gameStatus.levelAuthorName);
			beatmapJSON["songCover"] = String.IsNullOrEmpty(gameStatus.songCover) ? (JSONNode) JSONNull.CreateOrGet() : (JSONNode) new JSONString(gameStatus.songCover);
			beatmapJSON["songBPM"] = gameStatus.songBPM;
			beatmapJSON["noteJumpSpeed"] = gameStatus.noteJumpSpeed;
			beatmapJSON["songTimeOffset"] = new JSONNumber(gameStatus.songTimeOffset);
			beatmapJSON["start"] = gameStatus.start == 0 ? (JSONNode) JSONNull.CreateOrGet() : (JSONNode) new JSONNumber(gameStatus.start);
			beatmapJSON["paused"] = gameStatus.paused == 0 ? (JSONNode) JSONNull.CreateOrGet() : (JSONNode) new JSONNumber(gameStatus.paused);
			beatmapJSON["length"] = new JSONNumber(gameStatus.length);
			beatmapJSON["difficulty"] = stringOrNull(gameStatus.difficulty);
			beatmapJSON["notesCount"] = gameStatus.notesCount;
			beatmapJSON["bombsCount"] = gameStatus.bombsCount;
			beatmapJSON["obstaclesCount"] = gameStatus.obstaclesCount;
			beatmapJSON["maxScore"] = gameStatus.maxScore;
			beatmapJSON["maxRank"] = gameStatus.maxRank;
			beatmapJSON["environmentName"] = gameStatus.environmentName;
		}

		private void UpdatePerformanceJSON() {
			if (gameStatus.start == 0) {
				_statusJSON["performance"] = null;
				return;
			}

			if (_statusJSON["performance"] == null) _statusJSON["performance"] = new JSONObject();
			JSONObject performanceJSON = (JSONObject) _statusJSON["performance"];

			performanceJSON["score"] = gameStatus.score;
			performanceJSON["currentMaxScore"] = gameStatus.currentMaxScore;
			performanceJSON["rank"] = gameStatus.rank;
			performanceJSON["passedNotes"] = gameStatus.passedNotes;
			performanceJSON["hitNotes"] = gameStatus.hitNotes;
			performanceJSON["missedNotes"] = gameStatus.missedNotes;
			performanceJSON["lastNoteScore"] = gameStatus.lastNoteScore;
			performanceJSON["passedBombs"] = gameStatus.passedBombs;
			performanceJSON["hitBombs"] = gameStatus.hitBombs;
			performanceJSON["combo"] = gameStatus.combo;
			performanceJSON["maxCombo"] = gameStatus.maxCombo;
			performanceJSON["multiplier"] = gameStatus.multiplier;
			performanceJSON["multiplierProgress"] = gameStatus.multiplierProgress;
			performanceJSON["batteryEnergy"] = gameStatus.modBatteryEnergy || gameStatus.modInstaFail ? (JSONNode) new JSONNumber(gameStatus.batteryEnergy) : (JSONNode) JSONNull.CreateOrGet();
		}

		private void UpdateNoteCutJSON() {
			_noteCutJSON["noteID"] = gameStatus.noteID;
			_noteCutJSON["noteType"] = stringOrNull(gameStatus.noteType);
			_noteCutJSON["noteCutDirection"] = stringOrNull(gameStatus.noteCutDirection);
			_noteCutJSON["noteLine"] = gameStatus.noteLine;
			_noteCutJSON["noteLayer"] = gameStatus.noteLayer;
			_noteCutJSON["speedOK"] = gameStatus.speedOK;
			_noteCutJSON["directionOK"] = gameStatus.noteType == "Bomb" ? (JSONNode) JSONNull.CreateOrGet() : (JSONNode) new JSONBool(gameStatus.directionOK);
			_noteCutJSON["saberTypeOK"] = gameStatus.noteType == "Bomb" ? (JSONNode) JSONNull.CreateOrGet() : (JSONNode) new JSONBool(gameStatus.saberTypeOK);
			_noteCutJSON["wasCutTooSoon"] = gameStatus.wasCutTooSoon;
			_noteCutJSON["initialScore"] = gameStatus.initialScore == -1 ? (JSONNode) JSONNull.CreateOrGet() : (JSONNode) new JSONNumber(gameStatus.initialScore);
			_noteCutJSON["finalScore"] = gameStatus.finalScore == -1 ? (JSONNode) JSONNull.CreateOrGet() : (JSONNode) new JSONNumber(gameStatus.finalScore);
			_noteCutJSON["multiplier"] = gameStatus.cutMultiplier;
			_noteCutJSON["saberSpeed"] = gameStatus.saberSpeed;
			if (!_noteCutJSON["saberDir"].IsArray) _noteCutJSON["saberDir"] = new JSONArray();
			_noteCutJSON["saberDir"][0] = gameStatus.saberDirX;
			_noteCutJSON["saberDir"][1] = gameStatus.saberDirY;
			_noteCutJSON["saberDir"][2] = gameStatus.saberDirZ;
			_noteCutJSON["saberType"] = stringOrNull(gameStatus.saberType);
			_noteCutJSON["timeDeviation"] = gameStatus.timeDeviation;
			_noteCutJSON["cutDirectionDeviation"] = gameStatus.cutDirectionDeviation;
			if (!_noteCutJSON["cutPoint"].IsArray) _noteCutJSON["cutPoint"] = new JSONArray();
			_noteCutJSON["cutPoint"][0] = gameStatus.cutPointX;
			_noteCutJSON["cutPoint"][1] = gameStatus.cutPointY;
			_noteCutJSON["cutPoint"][2] = gameStatus.cutPointZ;
			if (!_noteCutJSON["cutNormal"].IsArray) _noteCutJSON["cutNormal"] = new JSONArray();
			_noteCutJSON["cutNormal"][0] = gameStatus.cutNormalX;
			_noteCutJSON["cutNormal"][1] = gameStatus.cutNormalY;
			_noteCutJSON["cutNormal"][2] = gameStatus.cutNormalZ;
			_noteCutJSON["cutDistanceToCenter"] = gameStatus.cutDistanceToCenter;
			_noteCutJSON["timeToNextBasicNote"] = gameStatus.timeToNextBasicNote;
		}

		private void UpdateModJSON() {
			if (_statusJSON["mod"] == null) _statusJSON["mod"] = new JSONObject();
			JSONObject modJSON = (JSONObject) _statusJSON["mod"];

			modJSON["multiplier"] = gameStatus.modifierMultiplier;
			modJSON["obstacles"] = gameStatus.modObstacles == null || gameStatus.modObstacles == "NoObstacles" ? (JSONNode) new JSONBool(false) : (JSONNode) new JSONString(gameStatus.modObstacles);
			modJSON["instaFail"] = gameStatus.modInstaFail;
			modJSON["noFail"] = gameStatus.modNoFail;
			modJSON["batteryEnergy"] = gameStatus.modBatteryEnergy;
			modJSON["batteryLives"] = gameStatus.modBatteryEnergy || gameStatus.modInstaFail ? (JSONNode) new JSONNumber(gameStatus.batteryLives) : (JSONNode) JSONNull.CreateOrGet();
			modJSON["disappearingArrows"] = gameStatus.modDisappearingArrows;
			modJSON["noBombs"] = gameStatus.modNoBombs;
			modJSON["songSpeed"] = gameStatus.modSongSpeed;
			modJSON["songSpeedMultiplier"] = gameStatus.songSpeedMultiplier;
			modJSON["noArrows"] = gameStatus.modNoArrows;
			modJSON["ghostNotes"] = gameStatus.modGhostNotes;
			modJSON["failOnSaberClash"] = gameStatus.modFailOnSaberClash;
			modJSON["strictAngles"] = gameStatus.modStrictAngles;
			modJSON["fastNotes"] = gameStatus.modFastNotes;
		}

		private void UpdatePlayerSettingsJSON() {
			if (_statusJSON["playerSettings"] == null) _statusJSON["playerSettings"] = new JSONObject();
			JSONObject playerSettingsJSON = (JSONObject) _statusJSON["playerSettings"];

			playerSettingsJSON["staticLights"] = gameStatus.staticLights;
			playerSettingsJSON["leftHanded"] = gameStatus.leftHanded;
			playerSettingsJSON["playerHeight"] = gameStatus.playerHeight;
			playerSettingsJSON["sfxVolume"] = gameStatus.sfxVolume;
			playerSettingsJSON["reduceDebris"] = gameStatus.reduceDebris;
			playerSettingsJSON["noHUD"] = gameStatus.noHUD;
			playerSettingsJSON["advancedHUD"] = gameStatus.advancedHUD;
			playerSettingsJSON["autoRestart"] = gameStatus.autoRestart;
		}

		private void UpdateBeatmapEventJSON() {
			_beatmapEventJSON["type"] = gameStatus.beatmapEventType;
			_beatmapEventJSON["value"] = gameStatus.beatmapEventValue;
		}

		private JSONNode stringOrNull(string str) {
			return str == null ? (JSONNode) JSONNull.CreateOrGet() : (JSONNode) new JSONString(str);
		}
	}

	public class ChangedProperties {
		public static readonly ChangedProperties AllButNoteCut = new ChangedProperties(true, true, true, false, true, false);
		public static readonly ChangedProperties Game = new ChangedProperties(true, false, false, false, false, false);
		public static readonly ChangedProperties Beatmap = new ChangedProperties(false, true, false, false, false, false);
		public static readonly ChangedProperties Performance = new ChangedProperties(false, false, true, false, false, false);
		public static readonly ChangedProperties PerformanceAndNoteCut = new ChangedProperties(false, false, true, true, false, false);
		public static readonly ChangedProperties Mod = new ChangedProperties(false, false, false, false, true, false);
		public static readonly ChangedProperties BeatmapEvent = new ChangedProperties(false, false, false, false, false, true);

		public readonly bool game;
		public readonly bool beatmap;
		public readonly bool performance;
		public readonly bool noteCut;
		public readonly bool mod;
		public readonly bool beatmapEvent;

		public ChangedProperties(bool game, bool beatmap, bool performance, bool noteCut, bool mod, bool beatmapEvent) {
			this.game = game;
			this.beatmap = beatmap;
			this.performance = performance;
			this.noteCut = noteCut;
			this.mod = mod;
			this.beatmapEvent = beatmapEvent;
		}
	};
}
