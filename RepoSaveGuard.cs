using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("REPO Save Guard")]
[assembly: System.Reflection.AssemblyVersion("1.0.2.0")]

namespace RepoSaveGuard
{
    public sealed class SaveInfo
    {
        public int Level;
        public string Team;
    }

    // ES3 is read only. The original encrypted bytes are always copied unchanged.
    public static class SaveReader
    {
        private sealed class DataOnlyTypeResolver : JavaScriptTypeResolver
        {
            // ES3 uses __type as data. Never instantiate types named by a save file.
            public override Type ResolveType(string id) { return typeof(Dictionary<string, object>); }
            public override string ResolveTypeId(Type type) { return null; }
        }
        public static SaveInfo Read(byte[] bytes)
        {
            if (bytes.Length < 32 || bytes.Length > 32 * 1024 * 1024 || bytes.Length % 16 != 0)
                throw new InvalidDataException("Неполный или неизвестный формат ES3.");
            byte[] iv = bytes.Take(16).ToArray();
            byte[] plain;
            using (var aes = Aes.Create())
            using (var key = new Rfc2898DeriveBytes("Why would you want to cheat?... :o It's no fun. :') :'D", iv, 100))
            {
                aes.Key = key.GetBytes(16);
                aes.IV = iv;
                using (var decrypt = aes.CreateDecryptor())
                    plain = decrypt.TransformFinalBlock(bytes, 16, bytes.Length - 16);
            }
            if (plain.Length > 2 && plain[0] == 31 && plain[1] == 139)
            {
                using (var input = new MemoryStream(plain))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    byte[] chunk = new byte[8192];
                    int count;
                    while ((count = gzip.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        output.Write(chunk, 0, count);
                        if (output.Length > 32 * 1024 * 1024) throw new InvalidDataException("Сейв слишком большой.");
                    }
                    plain = output.ToArray();
                }
            }
            var serializer = new JavaScriptSerializer(new DataOnlyTypeResolver()) { MaxJsonLength = 32 * 1024 * 1024 };
            var root = serializer.DeserializeObject(Encoding.UTF8.GetString(plain)) as Dictionary<string, object>;
            var dictionaries = (Dictionary<string, object>)((Dictionary<string, object>)root["dictionaryOfDictionaries"])["value"];
            var stats = (Dictionary<string, object>)dictionaries["runStats"];
            int level = Convert.ToInt32(stats["level"]);
            if (level < 0) throw new InvalidDataException("Некорректный уровень.");
            string team = "";
            if (root.ContainsKey("teamName"))
                team = Convert.ToString(((Dictionary<string, object>)root["teamName"])["value"]);
            return new SaveInfo { Level = level, Team = team };
        }

        public static byte[] ReadExclusive(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (file.Length > 32 * 1024 * 1024) throw new InvalidDataException("Сейв слишком большой.");
                using (var memory = new MemoryStream()) { file.CopyTo(memory); return memory.ToArray(); }
            }
        }
        public static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }
    }

    public sealed class Slot
    {
        public string Id, Hash, BackupFile, Team = "", Status = "Ожидание";
        public int Level;
        // The game displays the run's zero-based level index plus one.
        public long DisplayLevel { get { return (long)Level + 1; } }
        public DateTime SavedAt;
        internal string Candidate;
        internal DateTime CandidateSince;
        internal DateTime? MissingSince;
        internal string LastError;
    }

    public sealed class Guard
    {
        public readonly string Source, Storage;
        public readonly Dictionary<string, Slot> Slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
        public Action<string> Log = delegate { };
        private readonly TimeSpan stable, missing;
        private static readonly Regex SlotName = new Regex(@"^REPO_SAVE_\d{4}_\d{2}_\d{2}_\d{2}_\d{2}_\d{2}$", RegexOptions.CultureInvariant);

        public Guard(string source, string storage, double stableSeconds = 2, double missingSeconds = 4)
        {
            Source = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            Storage = Path.GetFullPath(storage).TrimEnd(Path.DirectorySeparatorChar);
            if (Source.Equals(Storage, StringComparison.OrdinalIgnoreCase) || Storage.StartsWith(Source + "\\", StringComparison.OrdinalIgnoreCase)
                || Source.StartsWith(Storage + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Папки сейвов и резервных копий должны находиться отдельно.");
            stable = TimeSpan.FromSeconds(stableSeconds);
            missing = TimeSpan.FromSeconds(missingSeconds);
            Directory.CreateDirectory(Storage);
            foreach (string directory in Directory.GetDirectories(Storage))
            {
                string id = Path.GetFileName(directory);
                if (!SlotName.IsMatch(id) || IsLink(directory)) continue;
                foreach (string file in Directory.GetFiles(directory, "*.es3").OrderByDescending(Path.GetFileName))
                {
                    try
                    {
                        if (IsLink(file)) continue;
                        byte[] data = SaveReader.ReadExclusive(file);
                        SaveInfo info = SaveReader.Read(data);
                        Slots[id] = new Slot { Id = id, Hash = SaveReader.Hash(data), Level = info.Level, Team = info.Team,
                            BackupFile = file, SavedAt = File.GetLastWriteTime(file), Status = "Копия загружена" };
                        break;
                    }
                    catch { /* An incomplete/corrupt latest version must not hide older valid versions. */ }
                }
            }
        }

        private static bool IsLink(string path) { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        private string MainFile(string id) { return Path.Combine(Source, id, id + ".es3"); }

        public void Tick(DateTime now)
        {
            // Do not resurrect every save while a drive / whole source directory is unavailable.
            if (!Directory.Exists(Source) || IsLink(Source))
                throw new IOException("Папка сейвов недоступна. Восстановление приостановлено: " + Source);
            foreach (string dir in Directory.GetDirectories(Source))
            {
                string id = Path.GetFileName(dir);
                if (SlotName.IsMatch(id) && !IsLink(dir) && !Slots.ContainsKey(id)) Slots[id] = new Slot { Id = id };
            }
            foreach (Slot slot in Slots.Values.ToList())
            {
                try { ProcessSlot(slot, now); slot.LastError = null; }
                catch (Exception ex)
                {
                    slot.Candidate = null;
                    slot.MissingSince = null;
                    slot.Status = "Ошибка: " + ex.Message;
                    if (slot.LastError != ex.Message) { Log(slot.Id + ": " + slot.Status); slot.LastError = ex.Message; }
                }
            }
        }

        private void ProcessSlot(Slot slot, DateTime now)
        {
            string path = MainFile(slot.Id);
            string dir = Path.GetDirectoryName(path);
            if (Directory.Exists(dir) && IsLink(dir)) throw new IOException("Ссылки на папки не поддерживаются.");
            // File.Exists hides access failures. GetAttributes distinguishes missing files from denied access.
            bool exists;
            try { File.GetAttributes(path); exists = true; }
            catch (FileNotFoundException) { exists = false; }
            catch (DirectoryNotFoundException) { exists = false; }
            if (!exists)
            {
                slot.Candidate = null;
                if (slot.BackupFile == null) { slot.Status = "Нет основного сейва"; return; }
                if (!slot.MissingSince.HasValue) slot.MissingSince = now;
                slot.Status = "Сейв исчез — ожидание восстановления";
                if (now - slot.MissingSince.Value >= missing) Restore(slot);
                return;
            }
            slot.MissingSince = null;
            if (IsLink(path)) throw new IOException("Ссылки на файлы не поддерживаются.");
            byte[] bytes = SaveReader.ReadExclusive(path);
            string hash = SaveReader.Hash(bytes);
            if (hash == slot.Hash) { slot.Candidate = null; slot.Status = "Защищён"; return; }
            SaveInfo info = SaveReader.Read(bytes);
            if (slot.BackupFile != null && info.Level < slot.Level)
            {
                slot.Status = "Уровень ниже копии — копия сохранена";
                return;
            }
            if (slot.Candidate != hash) { slot.Candidate = hash; slot.CandidateSince = now; }
            slot.Status = "Ожидание завершения записи";
            // Old existing files can be backed up immediately on first launch; new writes must settle.
            bool oldInitial = slot.BackupFile == null && now - File.GetLastWriteTimeUtc(path) >= stable;
            if (!oldInitial && now - slot.CandidateSince < stable) return;
            string folder = Path.Combine(Storage, slot.Id);
            Directory.CreateDirectory(folder);
            if (IsLink(folder)) throw new IOException("Ссылки в папке копий не поддерживаются.");
            string final = Path.Combine(folder, now.Ticks.ToString("D19") + "_" + Guid.NewGuid().ToString("N") + ".es3");
            AtomicNew(final, bytes);
            slot.Hash = hash; slot.Level = info.Level; slot.Team = info.Team;
            slot.BackupFile = final; slot.SavedAt = File.GetLastWriteTime(final); slot.Candidate = null;
            slot.Status = "Защищён";
            Log("Копия обновлена: " + slot.Id + " · уровень " + slot.DisplayLevel);
            // Keep 30 immutable versions for each slot. Delete only files created by this program.
            foreach (string old in Directory.GetFiles(folder, "*.es3")
                .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^\d{19}_[a-f0-9]{32}\.es3$"))
                .OrderByDescending(Path.GetFileName).Skip(30))
            {
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private void Restore(Slot slot)
        {
            byte[] bytes = SaveReader.ReadExclusive(slot.BackupFile);
            SaveReader.Read(bytes);
            if (SaveReader.Hash(bytes) != slot.Hash) throw new IOException("Резервная копия изменилась; восстановление отменено.");
            string dir = Path.Combine(Source, slot.Id);
            Directory.CreateDirectory(dir);
            if (IsLink(dir)) throw new IOException("Ссылки на папки не поддерживаются.");
            AtomicNew(MainFile(slot.Id), bytes); // Never overwrites a file that the game has recreated.
            slot.MissingSince = null; slot.Status = "Восстановлен — загрузите из меню";
            Log("Сейв восстановлен: " + slot.Id + ". Вернитесь в главное меню и загрузите его.");
        }

        private static void AtomicNew(string path, byte[] bytes)
        {
            string temp = Path.Combine(Path.GetDirectoryName(path), ".guard-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public sealed class MainWindow : Form
    {
        readonly string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "REPO Save Guard");
        readonly Label state = new Label(), pathLabel = new Label();
        readonly ListView list = new ListView();
        readonly TextBox journal = new TextBox();
        readonly Button pause = new Button();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly NotifyIcon tray = new NotifyIcon();
        Guard guard;
        bool running = true;
        string lastError;
        public MainWindow()
        {
            Text = "REPO Save Guard — защита сейвов";
            Size = new Size(1020, 670); MinimumSize = new Size(850, 580);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(23, 27, 34); ForeColor = Color.FromArgb(229, 234, 241);
            Icon = new Icon(typeof(MainWindow), "RepoSaveGuard.ico");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 7 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Controls.Add(layout);
            var title = new Label { Text = "REPO  /  SAVE GUARD", Font = new Font("Segoe UI", 21, FontStyle.Bold), Dock = DockStyle.Fill };
            layout.Controls.Add(title);
            state.Dock = DockStyle.Fill; state.ForeColor = Color.FromArgb(111, 224, 175); state.Padding = new Padding(0, 8, 0, 0);
            layout.Controls.Add(state);
            pathLabel.Dock = DockStyle.Fill; pathLabel.AutoEllipsis = true; pathLabel.ForeColor = Color.Silver;
            layout.Controls.Add(pathLabel);
            list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.GridLines = false;
            list.BackColor = Color.FromArgb(32, 38, 47); list.ForeColor = ForeColor;
            list.Columns.Add("Сейв / команда", 360); list.Columns.Add("Уровень", 70);
            list.Columns.Add("Копия обновлена", 150); list.Columns.Add("Состояние", 330);
            layout.Controls.Add(list);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = false };
            pause.Text = "Пауза защиты"; StyleButton(pause, 160); pause.Click += delegate { Toggle(); }; actions.Controls.Add(pause);
            AddButton(actions, "Папка копий", 155, delegate { System.Diagnostics.Process.Start("explorer.exe", guard.Storage); });
            AddButton(actions, "Папка сейвов", 155, delegate { System.Diagnostics.Process.Start("explorer.exe", guard.Source); });
            AddButton(actions, "Выбрать папку", 160, delegate { ChangeFolder(); });
            AddButton(actions, "Свернуть в трей", 170, delegate { Hide(); tray.Visible = true; });
            layout.Controls.Add(actions);
            journal.Dock = DockStyle.Fill; journal.Multiline = true; journal.ReadOnly = true; journal.ScrollBars = ScrollBars.Vertical;
            journal.BackColor = Color.FromArgb(19, 23, 29); journal.ForeColor = Color.Silver; journal.BorderStyle = BorderStyle.FixedSingle;
            layout.Controls.Add(journal);
            layout.Controls.Add(new Label { Text = "После смерти: главное меню → загрузить сейв. Перед ручным удалением сейва нажмите «Пауза».", Dock = DockStyle.Fill, Padding = new Padding(0, 9, 0, 0), AutoEllipsis = true });
            tray.Icon = Icon; tray.Text = "REPO Save Guard";
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add("Пауза / продолжить", null, delegate { Toggle(); });
            menu.Items.Add("Выход", null, delegate { Close(); }); tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
            Directory.CreateDirectory(home);
            string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow\semiwork\Repo\saves");
            string settings = Path.Combine(home, "source.txt");
            if (File.Exists(settings)) source = File.ReadAllText(settings, Encoding.UTF8).Trim();
            SetSource(source);
            timer.Interval = 750; timer.Tick += delegate { Poll(); };
            Shown += delegate { Poll(); timer.Start(); };
            FormClosed += delegate { timer.Stop(); tray.Dispose(); };
        }
        void StyleButton(Button b, int width)
        {
            b.Width = width; b.Height = 36; b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(46, 57, 70); b.ForeColor = ForeColor;
            b.FlatAppearance.BorderColor = Color.FromArgb(72, 90, 107);
        }
        void AddButton(Control panel, string title, int width, Action action)
        {
            var b = new Button { Text = title }; StyleButton(b, width);
            b.Click += delegate { try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, "REPO Save Guard"); } }; panel.Controls.Add(b);
        }
        void SetSource(string source)
        {
            string key = SaveReader.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(source).TrimEnd('\\').ToUpperInvariant())).Substring(0, 16);
            guard = new Guard(source, Path.Combine(home, "Backups", key));
            guard.Log = WriteLog;
            pathLabel.Text = "Сейвы: " + guard.Source;
            File.WriteAllText(Path.Combine(home, "source.txt"), guard.Source, Encoding.UTF8);
            WriteLog("Защита включена. Копии: " + guard.Storage);
        }
        void ChangeFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Выберите папку saves игры R.E.P.O.", SelectedPath = guard.Source })
                if (dialog.ShowDialog(this) == DialogResult.OK) { SetSource(dialog.SelectedPath); lastError = null; Poll(); }
        }
        void Toggle()
        {
            running = !running; pause.Text = running ? "Пауза защиты" : "Продолжить защиту";
            // Restart missing timers after pause, so a manual deletion is not restored immediately.
            foreach (var slot in guard.Slots.Values) { slot.MissingSince = null; slot.Candidate = null; }
            WriteLog(running ? "Защита включена." : "Пауза: копирование и восстановление остановлены."); Poll();
        }
        void WriteLog(string message)
        {
            journal.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            if (journal.TextLength > 24000) journal.Text = journal.Text.Substring(journal.TextLength - 18000);
            try { File.AppendAllText(Path.Combine(home, "guard.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine, Encoding.UTF8); } catch { }
        }
        void Poll()
        {
            if (!running) { state.Text = "●  Пауза — сейвы сейчас не защищаются"; state.ForeColor = Color.Orange; return; }
            try
            {
                guard.Tick(DateTime.UtcNow); lastError = null;
                bool errors = guard.Slots.Values.Any(s => s.Status.StartsWith("Ошибка") || s.Status.Contains("ниже"));
                int protectedCount = guard.Slots.Values.Count(s => s.BackupFile != null);
                state.Text = errors ? "●  Требуется внимание — смотрите состояние и журнал" : "●  Защита включена  ·  Сейвов с копией: " + protectedCount;
                state.ForeColor = errors ? Color.Orange : Color.FromArgb(111, 224, 175);
            }
            catch (Exception ex)
            {
                state.Text = "●  " + ex.Message; state.ForeColor = Color.Orange;
                if (lastError != ex.Message) { WriteLog(ex.Message); lastError = ex.Message; }
            }
            list.BeginUpdate(); list.Items.Clear();
            foreach (var s in guard.Slots.Values.OrderByDescending(s => s.Id))
                list.Items.Add(new ListViewItem(new[] { (String.IsNullOrEmpty(s.Team) ? "" : s.Team + " · ") + s.Id,
                    s.BackupFile == null ? "—" : s.DisplayLevel.ToString(), s.BackupFile == null ? "—" : s.SavedAt.ToString("dd.MM HH:mm:ss"), s.Status }));
            list.EndUpdate();
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--self-test")
            {
                try { SelfTest.Run(args[1]); return 0; }
                catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "FAILED.txt"), ex.ToString()); return 1; }
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (var mutex = new Mutex(true, "Local\\RepoSaveGuard-" + Environment.UserName, out created))
            {
                if (!created) { MessageBox.Show("REPO Save Guard уже запущен. Найдите его окно или значок в трее."); return 0; }
                try { Application.Run(new MainWindow()); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось запустить защиту"); return 1; }
            }
            return 0;
        }
    }

    public static class SelfTest
    {
        static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
        static byte[] Fixture(int level, int currency)
        {
            string json = "{\"teamName\":{\"value\":\"Тест\"},\"dictionaryOfDictionaries\":{\"value\":{\"runStats\":{\"level\":" + level + ",\"currency\":" + currency + "}}}}";
            using (var aes = Aes.Create())
            using (var key = new Rfc2898DeriveBytes("Why would you want to cheat?... :o It's no fun. :') :'D", aes.IV, 100))
            {
                aes.Key = key.GetBytes(16);
                using (var enc = aes.CreateEncryptor())
                {
                    byte[] p = Encoding.UTF8.GetBytes(json);
                    return aes.IV.Concat(enc.TransformFinalBlock(p, 0, p.Length)).ToArray();
                }
            }
        }
        public static void Run(string root)
        {
            root = Path.GetFullPath(root); Directory.CreateDirectory(root);
            string run = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(run, "saves"), backups = Path.Combine(run, "backups");
            string id = "REPO_SAVE_2026_11_19_20_00_00";
            string folder = Path.Combine(source, id), path = Path.Combine(folder, id + ".es3");
            Directory.CreateDirectory(folder);
            var reports = new List<string>();
            var now = DateTime.UtcNow.AddMinutes(1);
            var first = Fixture(8, 10); File.WriteAllBytes(path, first);
            var g = new Guard(source, backups); g.Tick(now); Check(g.Slots[id].Level == 8, "initial backup"); reports.Add("PASS initial valid backup");
            Check(g.Slots[id].DisplayLevel == 9 && new Slot { Level = 0 }.DisplayLevel == 1, "one-based game level numbering");
            reports.Add("PASS levels match the game menu: index 0 displays 1, index 8 displays 9");
            var next = Fixture(9, 25); File.WriteAllBytes(path, next); g.Tick(now.AddSeconds(1));
            Check(g.Slots[id].Level == 8, "settle before update"); g.Tick(now.AddSeconds(4));
            Check(g.Slots[id].Level == 9, "level advancement"); reports.Add("PASS debounce and successful level update");
            Directory.Delete(folder, true); g.Tick(now.AddSeconds(5)); Check(!File.Exists(path), "delete grace");
            g.Tick(now.AddSeconds(10)); Check(File.ReadAllBytes(path).SequenceEqual(next), "death restore"); reports.Add("PASS entire slot deleted: exact bytes restored");
            File.Delete(path); g.Tick(now.AddSeconds(11)); g.Tick(now.AddSeconds(16));
            Check(File.ReadAllBytes(path).SequenceEqual(next), "main file restore"); reports.Add("PASS primary file deleted: restored");
            File.WriteAllBytes(path, new byte[0]); g.Tick(now.AddSeconds(17));
            Check(g.Slots[id].Hash == SaveReader.Hash(next) && new FileInfo(path).Length == 0, "invalid not promoted"); reports.Add("PASS corrupt/empty file preserves backup, never overwrites existing file");
            File.WriteAllBytes(path, Fixture(1, 0)); g.Tick(now.AddSeconds(18)); g.Tick(now.AddSeconds(21));
            Check(g.Slots[id].Level == 9, "regression rejected"); reports.Add("PASS lower level never replaces checkpoint");
            File.WriteAllBytes(path, next);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { g.Tick(now.AddSeconds(22)); Check(g.Slots[id].Status.StartsWith("Ошибка"), "lock"); }
            g.Tick(now.AddSeconds(23)); Check(g.Slots[id].Status == "Защищён", "lock recovery"); reports.Add("PASS active writer lock and recovery");
            g = new Guard(source, backups); File.Delete(path); g.Tick(now.AddSeconds(24)); g.Tick(now.AddSeconds(29));
            Check(File.ReadAllBytes(path).SequenceEqual(next), "restart"); reports.Add("PASS restoration after program restart");
            File.Delete(path); g.Tick(now.AddSeconds(30)); var newer = Fixture(10, 30); File.WriteAllBytes(path, newer);
            g.Tick(now.AddSeconds(35)); g.Tick(now.AddSeconds(38)); Check(g.Slots[id].Level == 10, "recreate race"); reports.Add("PASS game recreation is not overwritten");
            for (int i = 0; i < 35; i++) { File.WriteAllBytes(path, Fixture(10, 100 + i)); g.Tick(now.AddSeconds(40 + 4 * i)); g.Tick(now.AddSeconds(43 + 4 * i)); }
            Check(Directory.GetFiles(Path.Combine(backups, id), "*.es3").Length == 30, "retention"); reports.Add("PASS history retention: 30 versions per slot");
            string bad = Path.Combine(backups, id, "9999999999999999999_" + Guid.NewGuid().ToString("N") + ".es3"); File.WriteAllBytes(bad, new byte[32]);
            g = new Guard(source, backups); Check(g.Slots[id].Level == 10, "corrupt latest fallback"); reports.Add("PASS corrupt latest backup falls back to previous valid version");
            Directory.Move(source, source + "-offline"); bool unavailable = false;
            try { g.Tick(now.AddMinutes(10)); } catch (IOException) { unavailable = true; }
            Check(unavailable && !Directory.Exists(source), "offline root"); reports.Add("PASS unavailable root is not recreated");
            // Read-only compatibility verification of this user's installed ES3 files.
            string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow\semiwork\Repo\saves");
            if (Directory.Exists(real)) foreach (string d in Directory.GetDirectories(real))
            {
                string f = Path.Combine(d, Path.GetFileName(d) + ".es3");
                if (File.Exists(f)) { SaveReader.Read(SaveReader.ReadExclusive(f)); reports.Add("PASS read-only ES3 compatibility: " + Path.GetFileName(d)); }
            }
            File.WriteAllLines(Path.Combine(root, "test-results.txt"), reports, Encoding.UTF8);
        }
    }
}
