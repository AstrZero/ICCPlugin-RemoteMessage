using System;
using System.IO;
using System.Media;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ink_Canvas.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace RemoteMessagePlugin
{
    [PluginEntrance]
    public class RemoteMessagePlugin : PluginBase
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private SoundPlayer? _soundPlayer;

        public override void Initialize(IPluginHost host, IServiceCollection services)
        {
            base.Initialize(host, services);
            Log("RemoteMessagePlugin 正在初始化...");
            InitAudioPlayer();
            StartHttpServer();
        }

        public override void Shutdown()
        {
            base.Shutdown();
            StopHttpServer();
            DisposeAudioPlayer();
            Log("RemoteMessagePlugin 已服务停止。");
        }

        private void InitAudioPlayer()
        {
            try
            {
                string wavPath = Path.Combine(PluginFolder, "notify-normal-new.wav");
                if (!File.Exists(wavPath))
                {
                    wavPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "notify-normal-new.wav");
                }

                if (File.Exists(wavPath))
                {
                    _soundPlayer = new SoundPlayer(wavPath);
                    _soundPlayer.LoadAsync(); // 预载入内存缓冲区，消解首次播放延迟
                    Log($"提示音预载入完成: {wavPath}");
                }
                else
                {
                    Log($"未找到提示音文件: {wavPath}");
                }
            }
            catch (Exception ex)
            {
                LogError("初始化音频播放器失败", ex);
            }
        }

        private void DisposeAudioPlayer()
        {
            try
            {
                _soundPlayer?.Stop();
                _soundPlayer?.Dispose();
                _soundPlayer = null;
            }
            catch { }
        }

        private void StartHttpServer()
        {
            try
            {
                bool started = false;
                string[] primaryPrefixes = new[] { "http://*:23000/", "http://+:23000/" };

                foreach (var prefix in primaryPrefixes)
                {
                    try
                    {
                        var testListener = new HttpListener();
                        testListener.Prefixes.Add(prefix);
                        testListener.Start();
                        _listener = testListener;
                        started = true;
                        Log($"HTTP 服务监听成功绑定前缀: {prefix}");
                        break;
                    }
                    catch
                    {
                        // 通配符绑定若无管理员权限则捕获并降级到 localhost
                    }
                }

                if (!started)
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add("http://localhost:23000/");
                    _listener.Prefixes.Add("http://127.0.0.1:23000/");
                    _listener.Start();
                    Log("HTTP 服务监听成功绑定 localhost:23000.");
                }

                _cts = new CancellationTokenSource();
                Task.Run(() => ListenLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                LogError("启动 23000 端口 HTTP 服务失败", ex);
            }
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => ProcessRequestAsync(context));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_listener == null || !_listener.IsListening) break;
                    LogError("HttpListener 处理客户端连接异常", ex);
                }
            }
        }

        private Task ProcessRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                string method = request.HttpMethod.ToUpperInvariant();
                string rawPath = request.Url?.AbsolutePath ?? "/";
                string decodedPath = WebUtility.UrlDecode(rawPath);

                // CORS 跨域支持
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (method == "OPTIONS")
                {
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.Close();
                    return Task.CompletedTask;
                }

                if (rawPath == "/" || rawPath.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] htmlBuffer = Encoding.UTF8.GetBytes(GetWebUiHtml());
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = htmlBuffer.Length;
                    response.OutputStream.Write(htmlBuffer, 0, htmlBuffer.Length);
                }
                else if (decodedPath.StartsWith("/notice/", StringComparison.OrdinalIgnoreCase))
                {
                    string content = decodedPath.Substring("/notice/".Length).Trim();
                    if (string.IsNullOrEmpty(content))
                    {
                        content = "收到一条远程通知消息";
                    }

                    TriggerNotice(content);

                    string responseMessage = $"Notice sent successfully: {content}";
                    byte[] buffer = Encoding.UTF8.GetBytes(responseMessage);
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.ContentType = "text/plain; charset=utf-8";
                    response.ContentLength64 = buffer.Length;
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                }
                else
                {
                    byte[] buffer = Encoding.UTF8.GetBytes("Usage: GET / or GET /notice/{内容}");
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    response.ContentType = "text/plain; charset=utf-8";
                    response.ContentLength64 = buffer.Length;
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                }
            }
            catch (Exception ex)
            {
                LogError("处理 HTTP 请求失败", ex);
                try
                {
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                }
                catch { }
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch { }
            }

            return Task.CompletedTask;
        }

        private void TriggerNotice(string content)
        {
            // 1. 发送灵动 Important 通知（标题为 content，副标题为 Important）
            var notificationService = GetService<INotificationService>();
            if (notificationService != null)
            {
                notificationService.Show(
                    title: content,
                    message: "Important",
                    level: NotificationLevel.Warning,
                    onClicked: () => { Log($"用户点击了通知详情: {content}"); }
                );
            }
            else
            {
                Log("INotificationService 服务未就绪。");
            }

            // 2. 播放提示音 notify-normal-new.wav
            PlayAudio();
        }

        private string GetWebUiHtml()
        {
            return @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>RemoteMessage</title>
  <link rel=""preconnect"" href=""https://fonts.googleapis.com"">
  <link rel=""preconnect"" href=""https://fonts.gstatic.com"" crossorigin>
  <link href=""https://fonts.googleapis.com/css2?family=Roboto:wght@400;500;700&display=swap"" rel=""stylesheet"">
  <style>
    :root {
      --md-sys-color-background: #fdfcff;
      --md-sys-color-on-background: #1a1c1e;
      --md-sys-color-primary: #006399;
      --md-sys-color-on-primary: #ffffff;
      --md-sys-color-outline: #74777f;
      --md-sys-color-outline-focus: #006399;
      --md-sys-color-on-surface-variant: #44474f;
      --md-sys-color-success: #1b6d36;
      --font-family: 'Roboto', -apple-system, BlinkMacSystemFont, ""Segoe UI"", sans-serif;
    }

    @media (prefers-color-scheme: dark) {
      :root {
        --md-sys-color-background: #111318;
        --md-sys-color-on-background: #e2e2e9;
        --md-sys-color-primary: #8ecdff;
        --md-sys-color-on-primary: #003453;
        --md-sys-color-outline: #8d9199;
        --md-sys-color-outline-focus: #8ecdff;
        --md-sys-color-on-surface-variant: #c3c6cf;
        --md-sys-color-success: #8cd899;
      }
    }

    * {
      box-sizing: border-box;
      margin: 0;
      padding: 0;
    }

    body {
      font-family: var(--font-family);
      background-color: var(--md-sys-color-background);
      color: var(--md-sys-color-on-background);
      min-height: 100vh;
      display: flex;
      justify-content: center;
      align-items: center;
      padding: 24px;
      transition: background-color 0.3s ease, color 0.3s ease;
    }

    .container {
      width: 100%;
      max-width: 480px;
      display: flex;
      flex-direction: column;
      gap: 32px;
      background: transparent;
    }

    .header-title {
      font-size: 2.25rem;
      font-weight: 400;
      line-height: 2.75rem;
      color: var(--md-sys-color-on-background);
    }

    .form-group {
      display: flex;
      flex-direction: column;
      gap: 24px;
    }

    .field-wrapper {
      position: relative;
      display: flex;
      flex-direction: column;
    }

    .field-input {
      width: 100%;
      height: 56px;
      background: transparent;
      border: 1px solid var(--md-sys-color-outline);
      border-radius: 4px;
      padding: 16px;
      font-size: 1rem;
      font-family: var(--font-family);
      color: var(--md-sys-color-on-background);
      outline: none;
      transition: border-color 0.2s ease;
    }

    .field-input:focus {
      border-color: var(--md-sys-color-outline-focus);
      border-width: 2px;
      padding: 15px;
    }

    .field-label {
      position: absolute;
      left: 12px;
      top: -10px;
      background-color: var(--md-sys-color-background);
      padding: 0 4px;
      font-size: 0.75rem;
      font-weight: 500;
      color: var(--md-sys-color-on-surface-variant);
      transition: color 0.2s ease, background-color 0.3s ease;
      pointer-events: none;
    }

    .field-input:focus ~ .field-label {
      color: var(--md-sys-color-outline-focus);
    }

    .btn-submit {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: 10px;
      height: 48px;
      padding: 0 24px;
      border: none;
      border-radius: 100px;
      background-color: var(--md-sys-color-primary);
      color: var(--md-sys-color-on-primary);
      font-family: var(--font-family);
      font-size: 0.875rem;
      font-weight: 500;
      letter-spacing: 0.1px;
      cursor: pointer;
      align-self: flex-start;
      transition: background-color 0.2s ease, box-shadow 0.2s ease, transform 0.1s ease;
      box-shadow: 0 1px 3px rgba(0,0,0,0.12);
    }

    .btn-submit:hover {
      box-shadow: 0 2px 6px rgba(0,0,0,0.2);
    }

    .btn-submit:active {
      transform: scale(0.98);
    }

    .btn-icon {
      width: 18px;
      height: 18px;
      fill: currentColor;
    }

    .status-toast {
      font-size: 0.875rem;
      color: var(--md-sys-color-success);
      opacity: 0;
      transition: opacity 0.3s ease;
      min-height: 20px;
    }

    .status-toast.show {
      opacity: 1;
    }
  </style>
</head>
<body>
  <div class=""container"">
    <h1 class=""header-title"">RemoteMessage</h1>
    
    <div class=""form-group"">
      <div class=""field-wrapper"">
        <input type=""text"" id=""contentInput"" class=""field-input"" placeholder="" "" autocomplete=""off"" />
        <label for=""contentInput"" class=""field-label"">Content</label>
      </div>

      <button id=""submitBtn"" class=""btn-submit"" type=""button"">
        <svg class=""btn-icon"" viewBox=""0 0 24 24"">
          <path d=""M2.01 21L23 12 2.01 3 2 10l15 2-15 2z""/>
        </svg>
        <span>Submit</span>
      </button>

      <div id=""statusToast"" class=""status-toast""></div>
    </div>
  </div>

  <script>
    const contentInput = document.getElementById('contentInput');
    const submitBtn = document.getElementById('submitBtn');
    const statusToast = document.getElementById('statusToast');

    async function sendNotice() {
      const val = contentInput.value.trim();
      if (!val) return;

      try {
        const res = await fetch('/notice/' + encodeURIComponent(val));
        if (res.ok) {
          showToast('Message sent successfully!');
          contentInput.value = '';
        } else {
          showToast('Failed to send message.');
        }
      } catch (err) {
        showToast('Error sending request.');
      }
    }

    function showToast(msg) {
      statusToast.textContent = msg;
      statusToast.classList.add('show');
      setTimeout(() => {
        statusToast.classList.remove('show');
      }, 3000);
    }

    submitBtn.addEventListener('click', sendNotice);
    contentInput.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') sendNotice();
    });
  </script>
</body>
</html>";
        }

        private void PlayAudio()
        {
            try
            {
                if (_soundPlayer != null)
                {
                    _soundPlayer.Play();
                }
                else
                {
                    // 若初始化未成功加载，尝试按需重新加载播放
                    string wavPath = Path.Combine(PluginFolder, "notify-normal-new.wav");
                    if (!File.Exists(wavPath))
                    {
                        wavPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "notify-normal-new.wav");
                    }

                    if (File.Exists(wavPath))
                    {
                        _soundPlayer = new SoundPlayer(wavPath);
                        _soundPlayer.Play();
                    }
                    else
                    {
                        Log($"未找到提示音文件: {wavPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("播放音频失败", ex);
            }
        }

        private void StopHttpServer()
        {
            try
            {
                _cts?.Cancel();
                if (_listener != null && _listener.IsListening)
                {
                    _listener.Stop();
                    _listener.Close();
                }
            }
            catch (Exception ex)
            {
                LogError("关闭 HTTP 服务失败", ex);
            }
        }
    }
}
