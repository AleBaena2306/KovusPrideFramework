# Net (`KovusPride.Net`)

`KpSocket`: WebSocket de texto que se consulta desde el hilo principal, sin callbacks entre hilos. Uso: `new KpSocket(url)`, comprobar `State` (`Connecting`, `Open`, `Closed`), `Send(texto)`, leer en `Update` con `while (socket.TryReceive(out var msg)) {...}` y `Dispose()` al terminar. En la web usa el WebSocket del navegador (`KovusPrideSocket.jslib`); en el Editor y Android, `ClientWebSocket` de .NET.
