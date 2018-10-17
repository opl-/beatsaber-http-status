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
		"scene": String, // See [Scenes](#scene-string)
		"mode": null | "SoloStandard" | "SoloOneSaber" | "SoloNoArrows" | "PartyStandard",
	},
	"beatmap": null | {
		"songName": String, // Song name
		"songNubName": String, // Song sub name
		"songAuthorName": String, // Song author name
		"songCover": null | String, // Base64 encoded PNG image of the song cover
		"songBPM": Number, // Song Beats Per Minute
		"songTimeOffset": Integer, // Time in millis of where in the song the beatmap starts
		"start": null | Integer, // UNIX timestamp in millis of when the map was started. Changes if the game is resumed.
		"paused": null | Integer, // If game is paused, UNIX timestamp in millis of when the map was paused. null otherwise.
		"length": Integer, // Length of map in millis
		"difficulty": "Easy" | "Normal" | "Hard" | "Expert" | "ExpertPlus", // Beatmap difficulty
		"notesCount": Integer, // Map cube count
		"obstaclesCount": Integer, // Map obstacle count
		"maxScore": Integer, // Max score obtainable on the map
	},
	"performance": null | {
		"score": Integer, // Current score
		"currentMaxScore": Integer, // Maximum score achievable at current passed notes
		"rank": "SSS" | "SS" | "S" | "A" | "B" | "C" | "D" | "E", // Current rank
		"passedNotes": Integer, // Amount of hit or missed cubes
		"hitNotes": Integer, // Amount of hit cubes
		"missedNotes": Integer, // Amount of missed cubes
		"passedBombs": Integer, // Amount of Hit or missed bombs
		"hitBombs": Integer, // Amount of hit bombs
		"combo": Integer, // Current combo
		"maxCombo": Integer, // Max obtained combo
		"multiplier": Integer, // Current multiplier [1, 2, 4, 8]
		"multiplierProgress": Number, // Current multiplier progress [0..1)
	},
	"mod": {
		"obstacles": false | "FullHeightOnly" | "All", // No Obstacles (FullHeightOnly is not possible from UI)
		"noEnergy": Boolean, // No Fail
		"mirror": Boolean, // Mirror
	},
}
```

### Note cut object

```js
NoteCutObject = {
	"noteID": Integer, // ID of the note
	"noteType": "NoteA" | "NoteB" | "GhostNote" | "Bomb", // Type of note
	"noteCutDirection": "Up" | "Down" | "Left" | "Right" | "UpLeft" | "UpRight" | "DownLeft" | "DownRight" | "Any" | "None", // Direction the note is supposed to be cut in
	"speedOK": Boolean, // Cut speed was fast enough
	"directionOK": null | Boolean, // Note was cut in the correct direction. null for bombs.
	"saberTypeOK": null | Boolean, // Note was cut with the correct saber. null for bombs.
	"wasCutTooSoon": Boolean, // Note was cut too early
	"initalScore": null | Integer, // Score without multiplier for the cut. Doesn't include the score for keeping angle after cut. null for bombs.
	"finalScore": null | Integer, // Score without multiplier for the entire cut, including keeping angle after cut. Available in [`noteFullyCut` event](#notefullycut-event). null for bombs.
	"multiplier": Integer, // Multiplier at the time of cut
	"saberSpeed": Number, // Speed of the saber when the note was cut
	"saberDir": [ // Direction the saber was moving in when the note was cut
		Number, // X value
		Number, // Y value
		Number, // Z value
	],
	"saberType": "SaberA" | "SaberB", // Saber used to cut this note
	"swingRating": Number, // Game's swing rating
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

### Scene string

| Scene                 | Event       | Description
| --------------------- | ----------- | -----------
| `Init`                | `scene`     | Game start
| `Menu`                | `menu`      | Main menu (song selection, settings, beatmap result)
| `HealthWarning`       | `scene`     | Reminder for the player to take breaks
| `GameCore`            | `scene`     | Unknown. Activated after song selection.
| `StandardLevelLoader` | `scene`     | Loading environments? Loaded after `GameCore`
| `StandardLevel`       | `songStart` | Playing a beatmap

## Events

Events are broadcasted over the WebSocket. For message format, see [Event object](#event-object).

### `hello` event

Sent when the client connects to the WebSocket server.

Contains the full [Status object](#status-object).

### `scene` event

Fired when a scene other than `Menu` and `StandardLevel` is loaded. `Menu` fires the [`menu` event](#menu-event) and `StandardLevel` fires the [`songStart` event](#songstart-event).

Contains the full [Status object](#status-object).

### `songStart` event

Fired when the `StandardLevel` scene is activated.

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
