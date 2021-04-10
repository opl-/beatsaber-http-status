using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities.Async;
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

				// read game info from on game thread
				var stringifiedStatus = UnityMainThreadTaskScheduler.Factory.StartNew(() => Encoding.UTF8.GetBytes(statusManager.statusJSON.ToString())).Result;

				res.ContentLength64 = stringifiedStatus.Length;
				res.Close(stringifiedStatus, false);

				return;
			}

			res.StatusCode = 404;
			res.Close();
		}
	}

	public class StatusBroadcastBehavior : WebSocketBehavior {
		private StatusManager statusManager;
		private Task readyToWrite = Task.CompletedTask;
		private readonly CancellationTokenSource connectionClosed = new CancellationTokenSource();

		public void SetStatusManager(StatusManager statusManager) {
			this.statusManager = statusManager;
			statusManager.statusChange += OnStatusChange;
		}

		/// <summary>Queue data to send on the websocket in-order. This method is thread-safe.</summary>
		private void QueuedSend(string data) {
			var promise = new TaskCompletionSource<object>();
			var oldReadyToWrite = Interlocked.Exchange(ref readyToWrite, promise.Task);
			oldReadyToWrite.ContinueWith(t => {
				SendAsync(data, b => {
					promise.SetResult(null);
				});
			}, connectionClosed.Token, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}

		protected override void OnOpen() {
			UnityMainThreadTaskScheduler.Factory.StartNew(() => {
				JSONObject eventJSON = new JSONObject();
				eventJSON["event"] = "hello";
				eventJSON["time"] = new JSONNumber(Plugin.GetCurrentTime());
				eventJSON["status"] = statusManager.statusJSON;

				QueuedSend(eventJSON.ToString());
			}, connectionClosed.Token);
		}

		protected override void OnClose(CloseEventArgs e) {
			connectionClosed.Cancel();
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

			QueuedSend(eventJSON.ToString());
		}
	}
}
