using System;

namespace BeatSaberHTTPStatus {
	[Serializable]
	public class GameStatus {
		public string updateCause;

		public string scene = null;
		public string mode = null;

		// Beatmap
		public string songName = null;
		public string songSubName = null;
		public string songAuthorName = null;
		public string songCover = null;
		public float songBPM;
		public long songTimeOffset = 0;
		public long length = 0;
		public long start = 0;
		public long paused = 0;
		public string difficulty = null;
		public int notesCount = 0;
		public int obstaclesCount = 0;
		public int maxScore = 0;

		// Performance
		public int score = 0;
		public int currentMaxScore = 0;
		public string rank = "E";
		public int passedNotes = 0;
		public int hitNotes = 0;
		public int missedNotes = 0;
		public int lastNoteScore = 0;
		public int passedBombs = 0;
		public int hitBombs = 0;
		public int combo = 0;
		public int maxCombo = 0;
		public int multiplier = 0;
		public float multiplierProgress = 0;

		// Note cut
		public int noteID = -1;
		public string noteType = null;
		public string noteCutDirection = null;
		public bool speedOK = false;
		public bool directionOK = false;
		public bool saberTypeOK = false;
		public bool wasCutTooSoon = false;
		public int initialScore = -1;
		public int finalScore = -1;
		public int cutMultiplier = 0;
		public float saberSpeed = 0;
		public float saberDirX = 0;
		public float saberDirY = 0;
		public float saberDirZ = 0;
		public string saberType = null;
		public float swingRating = 0;
		public float timeDeviation = 0;
		public float cutDirectionDeviation = 0;
		public float cutPointX = 0;
		public float cutPointY = 0;
		public float cutPointZ = 0;
		public float cutNormalX = 0;
		public float cutNormalY = 0;
		public float cutNormalZ = 0;
		public float cutDistanceToCenter = 0;

		// Mods
		public string modObstacles = "None";
		public bool modNoEnergy = false;
		public bool modMirror = false;

		public void ResetMapInfo() {
			this.songName = null;
			this.songSubName = null;
			this.songAuthorName = null;
			this.songCover = null;
			this.songBPM = 0f;
			this.songTimeOffset = 0;
			this.length = 0;
			this.start = 0;
			this.paused = 0;
			this.difficulty = null;
			this.notesCount = 0;
			this.obstaclesCount = 0;
			this.maxScore = 0;
		}

		public void ResetPerformance() {
			this.score = 0;
			this.currentMaxScore = 0;
			this.rank = "E";
			this.passedNotes = 0;
			this.hitNotes = 0;
			this.missedNotes = 0;
			this.lastNoteScore = 0;
			this.passedBombs = 0;
			this.hitBombs = 0;
			this.combo = 0;
			this.maxCombo = 0;
			this.multiplier = 0;
			this.multiplierProgress = 0;
		}

		public void ResetNoteCut() {
			this.noteID = -1;
			this.noteType = null;
			this.noteCutDirection = null;
			this.speedOK = false;
			this.directionOK = false;
			this.saberTypeOK = false;
			this.wasCutTooSoon = false;
			this.initialScore = -1;
			this.finalScore = -1;
			this.cutMultiplier = 0;
			this.saberSpeed = 0;
			this.saberDirX = 0;
			this.saberDirY = 0;
			this.saberDirZ = 0;
			this.saberType = null;
			this.swingRating = 0;
			this.timeDeviation = 0;
			this.cutDirectionDeviation = 0;
			this.cutPointX = 0;
			this.cutPointY = 0;
			this.cutPointZ = 0;
			this.cutNormalX = 0;
			this.cutNormalY = 0;
			this.cutNormalZ = 0;
			this.cutDistanceToCenter = 0;
		}
	}
}
