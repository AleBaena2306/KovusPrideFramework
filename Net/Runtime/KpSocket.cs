using System;
using System.Collections.Concurrent;
using System.Text;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#else
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
#endif

namespace KovusPride.Net
{
    // WebSocket de texto que se consulta desde el hilo principal (Poll en Update), sin callbacks entre hilos.
    // Web: el WebSocket del navegador mediante KovusPrideSocket.jslib. Editor y Android: ClientWebSocket de .NET.
    public class KpSocket : IDisposable
    {
        public enum Status { Connecting, Open, Closed }

        public Status State { get; private set; } = Status.Connecting;
        public string CloseReason { get; private set; } = "";

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int KP_WSOpen(string url);
        [DllImport("__Internal")] private static extern int KP_WSState(int id);
        [DllImport("__Internal")] private static extern void KP_WSSend(int id, string text);
        [DllImport("__Internal")] private static extern string KP_WSRecv(int id);
        [DllImport("__Internal")] private static extern void KP_WSClose(int id);
        private readonly int id;

        public KpSocket(string url) { id = KP_WSOpen(url); }

        public bool TryReceive(out string msg)
        {
            msg = null;
            if (id <= 0) { State = Status.Closed; return false; }
            msg = KP_WSRecv(id);
            if (!string.IsNullOrEmpty(msg)) return true;
            int s = KP_WSState(id);
            State = s == 1 ? Status.Open : s == 0 ? Status.Connecting : Status.Closed;
            return false;
        }

        public void Send(string text) { if (id > 0 && State == Status.Open) KP_WSSend(id, text); }

        public void Dispose() { if (id > 0) KP_WSClose(id); State = Status.Closed; }
#else
        private readonly ClientWebSocket ws = new ClientWebSocket();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
        private readonly BlockingCollection<string> outbox = new BlockingCollection<string>();
        private volatile int status;   // 0 conectando, 1 abierto, 2 cerrado

        public KpSocket(string url)
        {
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            Task.Run(() => Run(new Uri(url)));
        }

        private async Task Run(Uri uri)
        {
            try
            {
                await ws.ConnectAsync(uri, cts.Token);
                status = 1;
                _ = Task.Run(SendLoop);
                var buffer = new byte[8192];
                var sb = new StringBuilder();
                while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    if (r.EndOfMessage) { inbox.Enqueue(sb.ToString()); sb.Length = 0; }
                }
            }
            catch (Exception e) { CloseReason = e.Message; }
            status = 2;
            outbox.CompleteAdding();
        }

        private async Task SendLoop()
        {
            try
            {
                foreach (var text in outbox.GetConsumingEnumerable(cts.Token))
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
                }
            }
            catch (Exception) { }
        }

        public bool TryReceive(out string msg)
        {
            if (inbox.TryDequeue(out msg)) return true;
            State = status == 1 ? Status.Open : status == 0 ? Status.Connecting : Status.Closed;
            return false;
        }

        public void Send(string text)
        {
            if (status != 1) return;
            try { outbox.Add(text); } catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            if (status == 2 && State == Status.Closed) return;
            State = Status.Closed;
            try
            {
                if (ws.State == WebSocketState.Open)
                    ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ContinueWith(_ => cts.Cancel());
                else cts.Cancel();
            }
            catch (Exception) { }
        }
#endif
    }
}
