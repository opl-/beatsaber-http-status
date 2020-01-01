using System;
using System.Text;
using SimpleJSON;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;

namespace BeatSaberHTTPStatus {
	public class HTTPServer {
		private int ServerPort = 6557;

		private HttpServer server;

		private StatusManager statusManager;

		public HTTPServer(StatusManager statusManager) {
			this.statusManager = statusManager;
		}

		public void InitServer() {
			server = new HttpServer(ServerPort);

			server.OnGet += (sender, e) => {
				OnHTTPGet(e);
			};

			server.AddWebSocketService<StatusBroadcastBehavior>("/socket", behavior => behavior.SetStatusManager(statusManager));

			BeatSaberHTTPStatus.Plugin.log.Info("Starting HTTP server on port " + ServerPort);
			server.Start();
		}

		public void StopServer() {
			BeatSaberHTTPStatus.Plugin.log.Info("Stopping HTTP server");
			server.Stop();
		}

		public void OnHTTPGet(HttpRequestEventArgs e) {
			var req = e.Request;
			var res = e.Response;

			if (req.RawUrl == "/status.json") {
				res.StatusCode = 200;
				res.ContentType = "application/json";
				res.ContentEncoding = Encoding.UTF8;

				res.WriteContent(Encoding.UTF8.GetBytes(statusManager.statusJSON.ToString()));

				return;
			}

			res.StatusCode = 404;
			res.WriteContent(new byte[] {});
		}
	}

	public class StatusBroadcastBehavior : WebSocketBehavior {
		private StatusManager statusManager;

		public void SetStatusManager(StatusManager statusManager) {
			this.statusManager = statusManager;

			statusManager.statusChange += OnStatusChange;
		}

		protected override void OnOpen() {
			JSONObject eventJSON = new JSONObject();

			eventJSON["event"] = "hello";
			eventJSON["time"] = new JSONNumber(Plugin.GetCurrentTime());
			eventJSON["status"] = statusManager.statusJSON;

			Send(eventJSON.ToString());
		}

		protected override void OnClose(CloseEventArgs e) {
			statusManager.statusChange -= OnStatusChange;
		}

		public void OnStatusChange(StatusManager statusManager, ChangedProperties changedProps, string cause) {
			JSONObject eventJSON = new JSONObject();
			eventJSON["event"] = cause;
			eventJSON["time"] = new JSONNumber(Plugin.GetCurrentTime());

			if (changedProps.game && changedProps.beatmap && changedProps.performance && changedProps.mod) {
				eventJSON["status"] = statusManager.statusJSON;
			} else {
				JSONObject status = new JSONObject();
				eventJSON["status"] = status;

				if (changedProps.game) status["game"] = statusManager.statusJSON["game"];
				if (changedProps.beatmap) status["beatmap"] = statusManager.statusJSON["beatmap"];
				if (changedProps.performance) status["performance"] = statusManager.statusJSON["performance"];
				if (changedProps.mod) {
					status["mod"] = statusManager.statusJSON["mod"];
					status["playerSettings"] = statusManager.statusJSON["playerSettings"];
				}
			}

			if (changedProps.noteCut) {
				eventJSON["noteCut"] = statusManager.noteCutJSON;
			}

			if (changedProps.beatmapEvent) {
				eventJSON["beatmapEvent"] = statusManager.beatmapEventJSON;
			}

			Send(eventJSON.ToString());
		}
	}
}
