# Beat Saber HTTP Status protocol

This document describes the protocol used by the Beat Saber HTTP Status plugin.

## Connecting

The web server by default runs on port `6557` (**B**eat **S**aber **ST**atus). Secure connections are currently not supported.

## Endpoints

### `GET /status.json`

Returns the [Status object](#status-object).

### `/socket`

Use this path to connect to the WebSocket. The WebSocket will send the [hello event](#hello-event) when connected and new [events](#events) as they happen.

## Standard objects

### Event object

```js
EventObject = {
	"event": String, // See [Events](#events)
	"status": Object, // See [Status object](#status-object). May be partial, depending on the event type.
}
```

### Status object

```js
StatusObject = {
	"game": {
		"pluginVersion": String, // Currently running version of the plugin
		"gameVersion": String, // Version of the game the current plugin version is targetting
		"scene": "Menu" | "Song", // Indicates player's current activity
		"mode": null | "SoloStandard" | "SoloOneSaber" | "SoloNoArrows" | "PartyStandard" | "PartyOneSaber" | "PartyNoArrows",
	},
	"beatmap": null | {
		"songName": String, // Song name
		"songSubName": String, // Song sub name
		"songAuthorName": String, // Song author name
		"levelAuthorName": String, // Beatmap author name
		"songCover": null | String, // Base64 encoded PNG image of the song cover
		"songHash": String, // Unique beatmap identifier. At most 32 characters long. Same for all difficulties.
		"songBPM": Number, // Song Beats Per Minute
		"noteJumpSpeed": Number, // Song note jump movement speed, how fast the notes move towards the player.
		"songTimeOffset": Integer, // Time in millis of where in the song the beatmap starts. Adjusted for song speed multiplier.
		"start": null | Integer, // UNIX timestamp in millis of when the map was started. Changes if the game is resumed. Might be altered by practice settings.
		"paused": null | Integer, // If game is paused, UNIX timestamp in millis of when the map was paused. null otherwise.
		"length": Integer, // Length of map in millis. Adjusted for song speed multiplier.
		"difficulty": "Easy" | "Normal" | "Hard" | "Expert" | "ExpertPlus", // Beatmap difficulty
		"notesCount": Integer, // Map cube count
		"bombsCount": Integer, // Map bomb count. Set even with No Bombs modifier enabled.
		"obstaclesCount": Integer, // Map obstacle count. Set even with No Obstacles modifier enabled.
		"maxScore": Integer, // Max score obtainable on the map with modifier multiplier
		"maxRank": "SSS" | "SS" | "S" | "A" | "B" | "C" | "D" | "E", // Max rank obtainable using current modifiers
		"environmentName": String, // Name of the environment this beatmap requested // TODO: list available names
	},
	"performance": null | {
		"score": Integer, // Current score with modifier multiplier
		"currentMaxScore": Integer, // Maximum score with modifier multiplier achievable at current passed notes
		"rank": "SSS" | "SS" | "S" | "A" | "B" | "C" | "D" | "E", // Current rank
		"passedNotes": Integer, // Amount of hit or missed cubes
		"hitNotes": Integer, // Amount of hit cubes
		"missedNotes": Integer, // Amount of missed cubes
		"passedBombs": Integer, // Amount of hit or missed bombs
		"hitBombs": Integer, // Amount of hit bombs
		"combo": Integer, // Current combo
		"maxCombo": Integer, // Max obtained combo
		"multiplier": Integer, // Current combo multiplier {1, 2, 4, 8}
		"multiplierProgress": Number, // Current combo multiplier progress [0..1)
		"batteryEnergy": null | Integer, // Current amount of battery lives left. null if Battery Energy and Insta Fail are disabled.
	},
	"mod": {
		"multiplier": Number, // Current score multiplier for gameplay modifiers
		"obstacles": false | "FullHeightOnly" | "All", // No Obstacles (FullHeightOnly is not possible from UI)
		"instaFail": Boolean, // Insta Fail
		"noFail": Boolean, // No Fail
		"batteryEnergy": Boolean, // Battery Energy
		"batteryLives": null | Integer, // Amount of battery energy available. 4 with Battery Energy, 1 with Insta Fail, null with neither enabled.
		"disappearingArrows": Boolean, // Disappearing Arrows
		"noBombs": Boolean, // No Bombs
		"songSpeed": "Normal" | "Slower" | "Faster", // Song Speed (Slower = 85%, Faster = 120%)
		"songSpeedMultiplier": Number, // Song speed multiplier. Might be altered by practice settings.
		"noArrows": Boolean, // No Arrows
		"ghostNotes": Boolean, // Ghost Notes
		"failOnSaberClash": Boolean, // Fail on Saber Clash (Hidden)
		"strictAngles": Boolean, // Strict Angles (Hidden. Requires more precise cut direction; changes max deviation from 60deg to 15deg)
		"fastNotes": Boolean, // Does something (Hidden)
	},
	"playerSettings": {
		"staticLights": Boolean, // Static lights
		"leftHanded": Boolean, // Left handed
		"playerHeight": Number, // Player's height
		"sfxVolume": Number, // Disable sound effects [0..1]
		"reduceDebris": Boolean, // Reduce debris
		"noHUD": Boolean, // No text and HUDs
		"advancedHUD": Boolean, // Advanced HUD
		"autoRestart" : Boolean, // Auto Restart on Fail
	},
}
```

### Note cut object

```js
NoteCutObject = {
	"noteID": Integer, // ID of the note
	"noteType": "NoteA" | "NoteB" | "GhostNote" | "Bomb", // Type of note
	"noteCutDirection": "Up" | "Down" | "Left" | "Right" | "UpLeft" | "UpRight" | "DownLeft" | "DownRight" | "Any" | "None", // Direction the note is supposed to be cut in
	"noteLine": Integer, // The horizontal position of the note, from left to right [0..3]
	"noteLayer": Integer, // The vertical position of the note, from bottom to top [0..2]
	"speedOK": Boolean, // Cut speed was fast enough
	"directionOK": null | Boolean, // Note was cut in the correct direction. null for bombs.
	"saberTypeOK": null | Boolean, // Note was cut with the correct saber. null for bombs.
	"wasCutTooSoon": Boolean, // Note was cut too early
	"initalScore": null | Integer, // Score without multipliers for the cut. Doesn't include the score for swinging after cut. null for bombs.
	"finalScore": null | Integer, // Score without multipliers for the entire cut, including score for swinging after cut. Available in [`noteFullyCut` event](#notefullycut-event). null for bombs.
	"multiplier": Integer, // Combo multiplier at the time of cut
	"saberSpeed": Number, // Speed of the saber when the note was cut
	"saberDir": [ // Direction the saber was moving in when the note was cut
		Number, // X value
		Number, // Y value
		Number, // Z value
	],
	"saberType": "SaberA" | "SaberB", // Saber used to cut this note
	"swingRating": Number, // Game's swing rating. Uses the before cut rating in noteCut events and after cut rating for noteFullyCut events. -1 for bombs.
	"timeDeviation": Number, // Time offset in seconds from the perfect time to cut a note
	"cutDirectionDeviation": Number, // Offset from the perfect cut angle in degrees
	"cutPoint": [ // Position of the point on the cut plane closests to the note center
		Number, // X value
		Number, // Y value
		Number, // Z value
	],
	"cutNormal": [ // Normal of the ideal plane to cut along
		Number, // X value
		Number, // Y value
		Number, // Z value
	],
	"cutDistanceToCenter": Number, // Distance from the center of the note to the cut plane
	"timeToNextBasicNote": Number, // Time until next note in seconds
}
```

### Beatmap event object

Basic references for event types and values can be found here: <https://steamcommunity.com/sharedfiles/filedetails/?id=1377190061>

```js
BeatmapEvent = {
	"type": Integer,
	"value": Integer,
}
```

## Events

Events are broadcasted over the WebSocket. For message format, see [Event object](#event-object).

### `hello` event

Sent when the client connects to the WebSocket server.

Contains the full [Status object](#status-object).

### `songStart` event

Fired when the `GameCore` scene is activated.

Contains the full [Status object](#status-object).

### `finished` event

Fired when the player finishes a beatmap.

Contains only the `performance` property of [Status object](#status-object).

### `failed` event

Fired when the player fails a beatmap.

Contains only the `performance` property of [Status object](#status-object).

### `menu` event

Fired when the `Menu` scene is activated.

Contains the full [Status object](#status-object).

### `pause` event

Fired when the beatmap is paused.

Contains only the `beatmap` property of [Status object](#status-object).

### `resume` event

Fired when the beatmap is resumed.

Contains only the `beatmap` property of [Status object](#status-object).

### `noteCut` event

Fired when a note is cut.

Contains only the `performance` property of [Status object](#status-object) and a `noteCut` property as described in [Note cut object](#note-cut-object).

Also see: [`noteFullyCut` event](#notefullycut-event).

### `noteFullyCut` event

Fired when the `AfterCutScoreBuffer` finishes, ie. the game finishes gathering data to calculated the cut exit score. The field `performance.lastNoteScore` is updated right before this event is fired. This even is not fired for bomb notes.

Contains only the `performance` property of [Status object](#status-object) and a `noteCut` property as described in [Note cut object](#note-cut-object).

### `noteMissed` event

Fired when a note is missed.

Contains only the `performance` property of [Status object](#status-object).

Contains `noteCut` property with an object value as described in [Note cut object](#note-cut-object) or `null` if the note wasn't cut at all.

### `bombCut` event

Fired when a bomb is cut.

Contains only the `performance` property of [Status object](#status-object) and a `noteCut` property as described in [Note cut object](#note-cut-object).

### `bombMissed` event

Fired when a bomb is missed.

Contains only the `performance` property of [Status object](#status-object).

### `obstacleEnter` event

Fired when the player enters an obstacle.

Contains only the `performance` property of [Status object](#status-object).

### `obstacleExit` event

Fired when the player exits an obstacle.

Contains only the `performance` property of [Status object](#status-object).

### `scoreChanged` event

Fired when the score changes.

Contains only the `performance` property of [Status object](#status-object).

### `beatmapEvent` event

Fired when a beatmap event is triggered. Beatmap events include changing light colors, light rotation speed and moving the square rings.

Contains only a `beatmapEvent` property as described in [Beatmap event object](#beatmap-event-object).
