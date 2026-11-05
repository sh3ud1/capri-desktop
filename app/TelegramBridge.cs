using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CapriDesktop
{
    [DataContract]
    internal sealed class TelegramSavedSettings
    {
        [DataMember(Name = "token")] internal string ProtectedToken;
        [DataMember(Name = "user_id")] internal long UserId;
    }

    [DataContract]
    internal sealed class TelegramEnvelope
    {
        [DataMember] internal bool ok;
        [DataMember] internal TelegramUpdate[] result;
    }

    [DataContract]
    internal sealed class TelegramUpdate
    {
        [DataMember] internal long update_id;
        [DataMember] internal TelegramMessage message;
    }

    [DataContract]
    internal sealed class TelegramMessage
    {
        [DataMember] internal TelegramUser from;
        [DataMember] internal TelegramChat chat;
        [DataMember] internal string text;
        [DataMember] internal TelegramUser forward_from;
        [DataMember] internal TelegramChat sender_chat;
        [DataMember] internal TelegramUser via_bot;
        [DataMember] internal long forward_date;
        [DataMember] internal TelegramForwardOrigin forward_origin;
        [DataMember] internal bool is_automatic_forward;
    }

    [DataContract]
    internal sealed class TelegramUser
    {
        [DataMember] internal long id;
        [DataMember] internal bool is_bot;
    }

    [DataContract]
    internal sealed class TelegramChat
    {
        [DataMember] internal long id;
        [DataMember] internal string type;
    }

    [DataContract]
    internal sealed class TelegramForwardOrigin
    {
        [DataMember] internal string type;
        [DataMember] internal TelegramUser sender_user;
        [DataMember] internal TelegramChat sender_chat;
    }

    // First release is deliberately read-only: no shell, arbitrary file access, or generic Capri tool dispatch.
    internal sealed class TelegramBridge : IDisposable
    {
        private readonly string token;
        private readonly long ownerId;
        private readonly object requestLock = new object();
        private readonly ManualResetEvent stopped = new ManualResetEvent(false);
        private HttpWebRequest activeRequest;
        private Thread worker;
        private volatile bool stopping;
        private long offset;

        internal TelegramBridge(string botToken, long allowedUserId)
        {
            if (string.IsNullOrEmpty(botToken) || allowedUserId <= 0)
                throw new ArgumentException("Telegram is not configured.");
            token = botToken;
            ownerId = allowedUserId;
        }

        internal void Start()
        {
            if (worker != null) return;
            stopping = false;
            stopped.Reset();
            worker = new Thread(Poll) { IsBackground = true, Name = "Capri Telegram" };
            worker.Start();
        }

        private void Poll()
        {
            while (!stopping)
            {
                try
                {
                    // Discard the backlog on startup: old messages must never become fresh remote instructions.
                    TelegramEnvelope latest = RequestUpdates(-1, 1, 0);
                    if (latest == null || !latest.ok) throw new WebException("Telegram did not accept getUpdates.");
                    if (latest.result != null && latest.result.Length > 0)
                        offset = latest.result[latest.result.Length - 1].update_id + 1;

                    while (!stopping)
                    {
                        TelegramEnvelope batch = RequestUpdates(offset, 20, 20);
                        if (batch == null || !batch.ok) throw new WebException("Telegram did not accept getUpdates.");
                        if (batch.result == null) continue;

                        foreach (TelegramUpdate update in batch.result)
                        {
                            if (stopping) break;
                            if (update == null) continue;
                            offset = Math.Max(offset, update.update_id + 1);
                            TelegramMessage message = update.message;
                            if (!Allowed(message)) continue;

                            string command = (message.text ?? "").Trim();
                            int split = command.IndexOfAny(new char[] { ' ', '\t', '\r', '\n' });
                            string head = split < 0 ? command : command.Substring(0, split);
                            string argument = split < 0 ? "" : command.Substring(split).Trim();
                            int mention = head.IndexOf('@');
                            if (mention >= 0) head = head.Substring(0, mention);
                            string verb = head.ToLowerInvariant();
                            Brain.Log("telegram", -1, "Accepted remote command " + verb);

                            if (verb == "/start" || verb == "/help")
                                Send(message.chat.id, "Capri is linked. Commands:\n/status — connection status\n/apps — list installed apps\n/open name — open an installed app\n/close name — close its windows\n/screenshot — send a screenshot\n/lock — lock this PC");
                            else if (verb == "/status")
                                Send(message.chat.id, "Capri is running. Telegram connection is active.");
                            else if (verb == "/apps")
                                Send(message.chat.id, Hands.TelegramApps());
                            else if (verb == "/open")
                                Send(message.chat.id, SecurityEnabled(Security.Open)
                                    ? Hands.TelegramOpenApp(argument)
                                    : "Opening apps is disabled in Capri's SECURITY settings.");
                            else if (verb == "/close")
                                Send(message.chat.id, SecurityEnabled(Security.Windows)
                                    ? Hands.TelegramCloseApp(argument)
                                    : "Closing apps is disabled in Capri's SECURITY settings.");
                            else if (verb == "/screenshot")
                            {
                                if (!SecurityEnabled(Security.Shortcuts))
                                    Send(message.chat.id, "Screenshots are disabled in Capri's SECURITY settings.");
                                else
                                {
                                    Picture picture = Computer.Snap(null);
                                    if (picture == null || picture.Jpeg == null)
                                        Send(message.chat.id, "Capri couldn't capture the screen. It may be locked or unavailable.");
                                    else SendPhoto(message.chat.id, picture.Jpeg);
                                }
                            }
                            else if (verb == "/lock")
                            {
                                if (!SecurityEnabled(Security.Power))
                                    Send(message.chat.id, "Locking the PC is disabled in Capri's SECURITY settings.");
                                else
                                    Send(message.chat.id, Hands.TelegramLock()
                                        ? "The PC has been locked."
                                        : "Windows couldn't lock the PC.");
                            }
                            else
                                Send(message.chat.id, "Unknown command. Send /help to see the available commands.");
                        }
                    }
                }
                catch (Exception)
                {
                    // Never log request URLs: Telegram bot URLs contain the secret token.
                    if (!stopping) stopped.WaitOne(5000);
                }
            }
        }

        private bool Allowed(TelegramMessage message)
        {
            return message != null && message.from != null && !message.from.is_bot
                && message.chat != null && message.chat.type == "private"
                && message.chat.id == message.from.id && message.from.id == ownerId
                && message.forward_from == null && message.sender_chat == null
                && message.via_bot == null && message.forward_date == 0
                && message.forward_origin == null && !message.is_automatic_forward
                && !string.IsNullOrEmpty(message.text);
        }

        private TelegramEnvelope RequestUpdates(long next, int limit, int wait)
        {
            string url = "https://api.telegram.org/bot" + token + "/getUpdates?limit=" + limit + "&timeout=" + wait;
            if (next != 0) url += "&offset=" + next;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = (wait + 10) * 1000;
            request.ReadWriteTimeout = (wait + 10) * 1000;
            request.Proxy = WebRequest.DefaultWebProxy;
            lock (requestLock)
            {
                if (stopping) return null;
                activeRequest = request;
            }
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                    return (TelegramEnvelope)new DataContractJsonSerializer(typeof(TelegramEnvelope)).ReadObject(stream);
            }
            finally
            {
                lock (requestLock) if (activeRequest == request) activeRequest = null;
            }
        }

        private void Send(long chatId, string text)
        {
            if (stopping) return;
            byte[] body = Encoding.UTF8.GetBytes("chat_id=" + Uri.EscapeDataString(chatId.ToString())
                + "&text=" + Uri.EscapeDataString(text));
            string url = "https://api.telegram.org/bot" + token + "/sendMessage";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = body.Length;
            request.Timeout = 15000;
            request.Proxy = WebRequest.DefaultWebProxy;
            lock (requestLock)
            {
                if (stopping) return;
                activeRequest = request;
            }
            try
            {
                using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) { }
            }
            finally
            {
                lock (requestLock) if (activeRequest == request) activeRequest = null;
            }
        }

        private static bool SecurityEnabled(int toggle)
        {
            return Security.Has(toggle) && Security.On(toggle);
        }

        private void SendPhoto(long chatId, byte[] jpeg)
        {
            if (stopping || jpeg == null || jpeg.Length == 0) return;
            string boundary = "----CapriTelegram" + Guid.NewGuid().ToString("N");
            byte[] body;
            using (MemoryStream payload = new MemoryStream())
            {
                byte[] header = Encoding.UTF8.GetBytes("--" + boundary + "\r\n"
                    + "Content-Disposition: form-data; name=\"chat_id\"\r\n\r\n"
                    + chatId.ToString() + "\r\n"
                    + "--" + boundary + "\r\n"
                    + "Content-Disposition: form-data; name=\"photo\"; filename=\"capri.jpg\"\r\n"
                    + "Content-Type: image/jpeg\r\n\r\n");
                payload.Write(header, 0, header.Length);
                payload.Write(jpeg, 0, jpeg.Length);
                byte[] footer = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");
                payload.Write(footer, 0, footer.Length);
                body = payload.ToArray();
            }

            string url = "https://api.telegram.org/bot" + token + "/sendPhoto";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "multipart/form-data; boundary=" + boundary;
            request.ContentLength = body.Length;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.Proxy = WebRequest.DefaultWebProxy;
            lock (requestLock)
            {
                if (stopping) return;
                activeRequest = request;
            }
            try
            {
                using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) { }
            }
            finally
            {
                lock (requestLock) if (activeRequest == request) activeRequest = null;
            }
        }

        public void Dispose()
        {
            stopping = true;
            stopped.Set();
            lock (requestLock)
            {
                if (activeRequest != null)
                    try { activeRequest.Abort(); } catch (Exception) { }
            }
            Thread current = worker;
            worker = null;
            if (current != null && current != Thread.CurrentThread && current.Join(3000))
                stopped.Dispose();
        }
    }

    internal sealed class TelegramSettingsDialog : Form
    {
        private readonly TextBox tokenBox;
        private readonly Label status;
        private readonly Button pair;
        private readonly Button confirm;
        private readonly long existingId;
        private string code;
        private long foundId;
        private Thread pairingThread;
        private volatile bool cancelled;
        private DateTime expires;

        internal string BotToken { get { return tokenBox.Text.Trim(); } }
        internal long UserId { get { return foundId > 0 ? foundId : existingId; } }

        internal TelegramSettingsDialog(string savedToken, long savedId)
        {
            existingId = savedId;
            foundId = savedId;
            Text = "Capri · Telegram";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new System.Drawing.Size(450, 260);

            Label heading = new Label { Left = 16, Top = 14, Width = 415, Height = 22, Text = "Telegram bot token" };
            tokenBox = new TextBox { Left = 16, Top = 40, Width = 415, UseSystemPasswordChar = true, Text = savedToken ?? "" };
            Label note = new Label { Left = 16, Top = 70, Width = 415, Height = 42,
                Text = "The token is encrypted on this PC. Never share it or commit it to GitHub." };
            status = new Label { Left = 16, Top = 115, Width = 415, Height = 38,
                Text = savedId > 0 ? "Linked Telegram user ID: " + savedId : "Not linked yet." };
            Button emergency = new Button { Left = 16, Top = 165, Width = 155, Height = 30,
                Text = "Emergency stop", Enabled = savedId > 0 && !string.IsNullOrEmpty(savedToken) };
            pair = new Button { Left = 16, Top = 210, Width = 155, Height = 30, Text = "Generate pairing code" };
            confirm = new Button { Left = 181, Top = 210, Width = 155, Height = 30, Text = "Save and connect", Enabled = savedId > 0 && !string.IsNullOrEmpty(savedToken) };
            Button cancel = new Button { Left = 341, Top = 210, Width = 90, Height = 30, Text = "Close", DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { heading, tokenBox, note, status, emergency, pair, confirm, cancel });
            emergency.Click += delegate
            {
                if (MessageBox.Show(this, "This stops Telegram remote access and removes its saved connection from this PC. You can set it up again later.",
                    "Emergency stop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    DialogResult = DialogResult.Abort;
                    Close();
                }
            };
            tokenBox.TextChanged += delegate { confirm.Enabled = UserId > 0 && !string.IsNullOrEmpty(BotToken); };
            pair.Click += PairClick;
            confirm.Click += delegate
            {
                if (UserId <= 0 || string.IsNullOrEmpty(BotToken)) return;
                DialogResult = DialogResult.OK;
                Close();
            };
            AcceptButton = confirm;
            CancelButton = cancel;
            FormClosing += delegate { cancelled = true; };
        }

        private void PairClick(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(BotToken)) { status.Text = "Paste the bot token first."; return; }
            if (pairingThread != null && pairingThread.IsAlive) return;
            code = MakeCode();
            expires = DateTime.UtcNow.AddMinutes(3);
            cancelled = false;
            foundId = 0;
            confirm.Enabled = false;
            pair.Enabled = false;
            status.Text = "Send this code to your bot in a private chat: " + code + " (valid for 3 minutes)";
            pairingThread = new Thread(PairPoll) { IsBackground = true, Name = "Capri Telegram pairing" };
            pairingThread.Start();
        }

        private static string MakeCode()
        {
            byte[] bytes = new byte[4];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return (10000000 + (BitConverter.ToUInt32(bytes, 0) % 90000000)).ToString();
        }

        private void PairPoll()
        {
            long next = 0;
            try
            {
                while (!cancelled && DateTime.UtcNow < expires)
                {
                    string url = "https://api.telegram.org/bot" + BotToken + "/getUpdates?timeout=10&limit=20"
                        + (next > 0 ? "&offset=" + next : "");
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.Timeout = 20000;
                    request.ReadWriteTimeout = 20000;
                    request.Proxy = WebRequest.DefaultWebProxy;
                    TelegramEnvelope batch;
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                        batch = (TelegramEnvelope)new DataContractJsonSerializer(typeof(TelegramEnvelope)).ReadObject(stream);
                    if (batch == null || !batch.ok || batch.result == null) continue;

                    foreach (TelegramUpdate update in batch.result)
                    {
                        if (update == null) continue;
                        next = Math.Max(next, update.update_id + 1);
                        TelegramMessage message = update.message;
                        if (message == null || message.from == null || message.from.is_bot || message.chat == null
                            || message.chat.type != "private" || message.chat.id != message.from.id
                            || message.forward_from != null || message.sender_chat != null || message.via_bot != null
                            || message.forward_date != 0 || message.forward_origin != null || message.is_automatic_forward)
                            continue;
                        if (!string.Equals((message.text ?? "").Trim(), code, StringComparison.Ordinal)) continue;

                        long id = message.from.id;
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke((Action)delegate
                            {
                                if (cancelled || DateTime.UtcNow >= expires) return;
                                foundId = id;
                                status.Text = "Account detected: " + id + ". Confirm on this PC to finish linking.";
                                confirm.Enabled = !string.IsNullOrEmpty(BotToken);
                                pair.Enabled = true;
                            });
                        return;
                    }
                }
                if (!cancelled && !IsDisposed && IsHandleCreated)
                    BeginInvoke((Action)delegate { status.Text = "Pairing code expired. Generate a new one."; pair.Enabled = true; });
            }
            catch (Exception)
            {
                if (!cancelled && !IsDisposed && IsHandleCreated)
                    try { BeginInvoke((Action)delegate { status.Text = "Couldn't reach Telegram. Check the token and connection."; pair.Enabled = true; }); }
                    catch (InvalidOperationException) { }
            }
        }
    }

    internal sealed partial class Companion
    {
        private TelegramBridge telegramBridge;

        private string TelegramSettingsPath
        {
            get { return Path.Combine(Path.GetDirectoryName(settingsPath), "telegram.json"); }
        }

        private TelegramSavedSettings ReadTelegramSettings()
        {
            try
            {
                using (FileStream stream = File.OpenRead(TelegramSettingsPath))
                    return (TelegramSavedSettings)new DataContractJsonSerializer(typeof(TelegramSavedSettings)).ReadObject(stream);
            }
            catch (Exception) { return null; }
        }

        private void SaveTelegramSettings(string token, long userId)
        {
            TelegramSavedSettings saved = new TelegramSavedSettings
            {
                ProtectedToken = Voice.Protect(token),
                UserId = userId
            };
            string path = TelegramSettingsPath;
            string temporary = path + ".new";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (FileStream stream = File.Create(temporary))
                new DataContractJsonSerializer(typeof(TelegramSavedSettings)).WriteObject(stream, saved);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private void StartTelegramBridge()
        {
            if (testing || telegramBridge != null) return;
            TelegramSavedSettings saved = ReadTelegramSettings();
            if (saved == null || saved.UserId <= 0 || string.IsNullOrEmpty(saved.ProtectedToken)) return;
            string token = Voice.Unprotect(saved.ProtectedToken);
            if (string.IsNullOrEmpty(token)) return;
            try
            {
                telegramBridge = new TelegramBridge(token, saved.UserId);
                telegramBridge.Start();
            }
            catch (Exception) { telegramBridge = null; }
        }

        private void StopTelegramBridge()
        {
            TelegramBridge current = telegramBridge;
            telegramBridge = null;
            if (current != null) current.Dispose();
        }

        private void ConfigureTelegram()
        {
            StopTelegramBridge();
            TelegramSavedSettings saved = ReadTelegramSettings();
            string token = saved == null ? null : Voice.Unprotect(saved.ProtectedToken);
            long userId = saved == null ? 0 : saved.UserId;
            bool restart = true;
            using (TelegramSettingsDialog dialog = new TelegramSettingsDialog(token, userId))
            {
                DialogResult result = dialog.ShowDialog(this);
                if (result == DialogResult.OK)
                {
                    try
                    {
                        SaveTelegramSettings(dialog.BotToken, dialog.UserId);
                    }
                    catch (Exception)
                    {
                        MessageBox.Show(this, "Couldn't save Telegram settings. Check that Capri can write to its data folder.",
                            "Capri · Telegram", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else if (result == DialogResult.Abort)
                {
                    restart = false;
                    try
                    {
                        if (File.Exists(TelegramSettingsPath)) File.Delete(TelegramSettingsPath);
                        MessageBox.Show(this, "Telegram remote access has been disabled on this PC.",
                            "Capri · Telegram", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception)
                    {
                        MessageBox.Show(this, "Telegram was stopped for this session, but its saved settings couldn't be deleted. Check the Capri data folder.",
                            "Capri · Telegram", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            if (restart) StartTelegramBridge();
        }
    }
}
