using System.Globalization;

namespace GrandPrixPlayer;

/// <summary>
/// Fenêtre de contrôle. Architecture :
///  - un lecteur mpv "maître" sans vidéo joue la piste audio choisie et sert d'horloge ;
///  - jusqu'à 4 lecteurs mpv "esclaves" sans audio affichent chacun une piste vidéo du même fichier
///    et sont recalés en continu sur l'horloge du maître (micro-variation de vitesse, ou seek si l'écart est grand).
/// </summary>
internal sealed class MainForm : Form
{
    public const int MaxViews = 4;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string GlyphPlay = "", GlyphPause = "", GlyphVol = "", GlyphMute = "", GlyphFull = "";

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly string? _initialFile;
    private readonly string? _logFile = Environment.GetEnvironmentVariable("GPP_LOG");

    private MpvPlayer? _master;
    private string? _file;
    private bool _fileReady, _pendingStart;
    private DateTime _pendingSince;
    private List<TrackInfo> _videoTracks = new();
    private List<TrackInfo> _audioOrder = new();
    private int _currentAid = -1;
    private readonly Dictionary<int, int> _principalAudio = new(); // piste vidéo → piste audio principale
    private readonly List<VideoView> _views = new();
    private readonly Dictionary<VideoView, VideoWindow> _windows = new();
    private bool _detached;
    private DateTime _lastSeek = DateTime.MinValue;
    private double _lastSeekTarget, _duration, _masterTime;
    private int _tick;
    private bool _busy, _shuttingDown, _closingDone, _fs, _showFsButton;
    private Rectangle _fsRestore;
    private FullscreenBar? _fsBar;
    private DateTime _fsBarLastNeeded;
    private FormWindowState _fsState;

    private readonly MenuStrip _menu = new();
    private readonly ToolStripMenuItem _mAudio = Theme.Item("&Audio");
    private readonly ToolStripMenuItem _miDetached, _miGlobal, _miGrid, _miMain, _miAddView;
    private readonly VideoGrid _grid = new();
    private readonly ListBox _audioList = new();
    private readonly Panel _bar = new();
    private readonly SeekBar _seek = new(), _vol = new();
    private readonly Button _btnPlay, _btnBack, _btnFwd, _btnMute, _btnAudio, _btnFs;
    private readonly Label _lblTime = new();
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };

    public MainForm(string? initialFile)
    {
        _initialFile = initialFile;
        _detached = _settings.Detached;

        Text = "Grand Prix Player";
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        AllowDrop = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(460, 220);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // ---- Menus ----
        _menu.Renderer = Theme.Renderer;
        _menu.BackColor = Theme.Bar;
        _menu.ForeColor = Theme.Text;

        var mFile = Theme.Item("&Fichier");
        mFile.DropDownItems.AddRange(new ToolStripItem[]
        {
            Theme.Item("&Ouvrir…", OpenDialog, "Ctrl+O"),
            new ToolStripSeparator(),
            Theme.Item("&Quitter", Close, "Alt+F4"),
        });

        var mPlay = Theme.Item("&Lecture");
        mPlay.DropDownItems.AddRange(new ToolStripItem[]
        {
            Theme.Item("Lecture / Pause", TogglePause, "Espace"),
            Theme.Item("Reculer de 10 s", () => SeekRelative(-10), "←"),
            Theme.Item("Avancer de 10 s", () => SeekRelative(10), "→"),
            new ToolStripSeparator(),
            Theme.Item("Volume +", () => ChangeVolume(5), "↑"),
            Theme.Item("Volume −", () => ChangeVolume(-5), "↓"),
            Theme.Item("Muet", ToggleMute, "M"),
        });

        var mVideo = Theme.Item("&Vidéo");
        _miDetached = Theme.Item("Mode &déporté (une fenêtre par vidéo)", () => _ = SetDetachedAsync(true), "Ctrl+D");
        _miGlobal = Theme.Item("Mode fenêtre &unique", () => _ = SetDetachedAsync(false), "Ctrl+D");
        _miGrid = Theme.Item("Disposition &grille", () => SetLayout(GridLayoutMode.Grid));
        _miMain = Theme.Item("Disposition &principale + vignettes", () => SetLayout(GridLayoutMode.Main));
        _miAddView = Theme.Item("&Ajouter une vue", AddView, "Ctrl+N");
        mVideo.DropDownItems.AddRange(new ToolStripItem[]
        {
            _miDetached, _miGlobal, new ToolStripSeparator(), _miGrid, _miMain, new ToolStripSeparator(),
            _miAddView, Theme.Item("&Plein écran", () => ToggleFullscreen(this), "F"),
        });

        var mHelp = Theme.Item("&?");
        mHelp.DropDownItems.Add(Theme.Item("&Raccourcis clavier", ShowHelp, "F1"));

        _menu.Items.AddRange(new ToolStripItem[] { mFile, mPlay, mVideo, _mAudio, mHelp });

        // ---- Barre de contrôle ----
        _bar.Dock = DockStyle.Bottom;
        _bar.BackColor = Theme.Bar;
        _btnBack = MakeButton("−10", "Reculer de 10 s (←)", () => SeekRelative(-10), false);
        _btnPlay = MakeButton(GlyphPlay, "Lecture / Pause (Espace)", TogglePause, true);
        _btnFwd = MakeButton("+10", "Avancer de 10 s (→)", () => SeekRelative(10), false);
        _btnMute = MakeButton(GlyphVol, "Muet (M)", ToggleMute, true);
        _btnFs = MakeButton(GlyphFull, "Plein écran (F)", () => ToggleFullscreen(this), true);
        _btnAudio = MakeButton("Audio : —", "Choisir la piste audio (touches 1 à 9)", ShowAudioPopup, false);
        _btnAudio.TextAlign = ContentAlignment.MiddleLeft;
        _btnAudio.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _lblTime.ForeColor = Theme.Text;
        _lblTime.TextAlign = ContentAlignment.MiddleLeft;
        _lblTime.Font = new Font("Segoe UI", 10f);
        _lblTime.Text = "0:00:00 / 0:00:00";
        _seek.Scrubbing += v => _lblTime.Text = $"{Fmt(v)} / {Fmt(_duration)}";
        _seek.Committed += SeekTo;
        _vol.Maximum = 100;
        _vol.FillColor = Theme.Text;
        _vol.Scrubbing += v => SetVolume((int)Math.Round(v), false);
        _bar.Controls.AddRange(new Control[] { _seek, _btnBack, _btnPlay, _btnFwd, _lblTime, _btnAudio, _btnMute, _vol, _btnFs });
        _bar.Resize += (_, _) => LayoutBar();

        // ---- Zone centrale ----
        _grid.Dock = DockStyle.Fill;
        _grid.Mode = _settings.Layout;
        _grid.GridX = _settings.GridX;
        _grid.GridY = _settings.GridY;
        _grid.MainX = _settings.MainX;

        _audioList.Dock = DockStyle.Fill;
        _audioList.BackColor = Theme.Bg;
        _audioList.ForeColor = Theme.Text;
        _audioList.BorderStyle = BorderStyle.None;
        _audioList.IntegralHeight = false;
        _audioList.DrawMode = DrawMode.OwnerDrawFixed;
        _audioList.Font = new Font("Segoe UI", 12f);
        _audioList.ItemHeight = LogicalToDeviceUnits(34);
        _audioList.DrawItem += DrawAudioItem;
        _audioList.MouseClick += (_, e) =>
        {
            int i = _audioList.IndexFromPoint(e.Location);
            if (i >= 0) SelectAudioSlot(i + 1);
        };

        Controls.Add(_grid);
        Controls.Add(_audioList);
        Controls.Add(_bar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;
        _grid.BringToFront();
        _audioList.BringToFront();

        _timer.Tick += (_, _) => OnTick();

        ApplyModeUi();
        RefreshAudioUi();
        SetVolume(_settings.Volume, false);
        UpdateMuteUi();
    }

    // =====================================================================
    //  Démarrage / arrêt
    // =====================================================================

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            _master = new MpvPlayer(new Dictionary<string, string>
            {
                ["vo"] = "null",
                ["vid"] = "no",
                ["sid"] = "no",
                ["pause"] = "yes",
                ["keep-open"] = "yes",
                ["hr-seek"] = "yes",
                ["volume"] = _settings.Volume.ToString(Inv),
                ["mute"] = _settings.Muted ? "yes" : "no",
                ["input-default-bindings"] = "no",
                ["osc"] = "no",
                ["load-scripts"] = "no",
                ["ytdl"] = "no",
                ["terminal"] = "no",
                ["audio-client-name"] = "Grand Prix Player",
                ["demuxer-max-bytes"] = "64MiB",
                ["demuxer-max-back-bytes"] = "32MiB",
            });
            _master.FileLoadedCallback = () => BeginInvoke(SetupAfterLoad);
            _master.LoadFailedCallback = msg => BeginInvoke(() =>
                MessageBox.Show(this, $"Impossible de lire le fichier :\n{_file}\n\n{msg}", "Grand Prix Player",
                    MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            MessageBox.Show(this,
                "La bibliothèque libmpv-2.dll (64 bits) est introuvable ou invalide.\n\n" +
                "Placez libmpv-2.dll dans le même dossier que GrandPrixPlayer.exe (voir README).",
                "Grand Prix Player", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _timer.Start();
        if (_initialFile != null) _ = OpenAsync(_initialFile);
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closingDone) { base.OnFormClosing(e); return; }
        e.Cancel = true;
        if (_shuttingDown) return;
        _shuttingDown = true;
        _timer.Stop();
        SaveSettings();
        await DestroyAllViewsAsync();
        var m = _master;
        _master = null;
        if (m != null) await Task.Run(m.Dispose);
        _closingDone = true;
        Close();
    }

    private void SaveSettings()
    {
        var b = _fs ? _fsRestore : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (_detached) _settings.MainDetached = WinBounds.From(b); else _settings.MainGlobal = WinBounds.From(b);
        _settings.Detached = _detached;
        _settings.Layout = _grid.Mode;
        _settings.GridX = _grid.GridX;
        _settings.GridY = _grid.GridY;
        _settings.MainX = _grid.MainX;
        SaveDetachedBounds();
        _settings.Save();
    }

    private void SaveDetachedBounds()
    {
        for (int i = 0; i < _views.Count; i++)
        {
            if (!_windows.TryGetValue(_views[i], out var w)) continue;
            while (_settings.DetachedWindows.Count <= i) _settings.DetachedWindows.Add(null);
            _settings.DetachedWindows[i] = WinBounds.From(w.NormalBounds);
        }
    }

    // =====================================================================
    //  Ouverture d'un fichier
    // =====================================================================

    private void OpenDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Ouvrir une vidéo",
            Filter = "Vidéos|*.mkv;*.mp4;*.m4v;*.ts;*.m2ts;*.mts;*.mov;*.avi;*.webm;*.wmv|Tous les fichiers|*.*",
            InitialDirectory = _settings.LastFolder ?? "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _ = OpenAsync(dlg.FileName);
    }

    private async Task OpenAsync(string path)
    {
        if (_master == null || _busy) return;
        _busy = true;
        try
        {
            if (_fs) ToggleMainFullscreen();
            await DestroyAllViewsAsync();
            _fileReady = false;
            _pendingStart = false;
            _videoTracks = new();
            _audioOrder = new();
            _currentAid = -1;
            _duration = 0;
            _masterTime = 0;
            RefreshAudioUi();
            _file = path;
            _settings.LastFolder = Path.GetDirectoryName(path);
            Text = $"{Path.GetFileName(path)} — Grand Prix Player";
            _master.Set("pause", "yes");
            _master.Set("vid", "no");
            _master.ResetLoaded();
            _master.Command("loadfile", path, "replace");
        }
        finally { _busy = false; }
    }

    /// <summary>Appelé une fois que le maître a analysé le fichier.</summary>
    private void SetupAfterLoad()
    {
        if (_master == null || _file == null || IsDisposed || _shuttingDown) return;
        var tracks = _master.ReadTracks();
        _videoTracks = tracks.Where(t => t.Type == "video" && !t.AlbumArt).ToList();
        _audioOrder = ApplyPreferredOrder(tracks.Where(t => t.Type == "audio").ToList());
        _duration = _master.GetDouble("duration") ?? 0;

        if (_audioOrder.Count > 0) SelectAudioSlot(1, osd: false);
        else _master.Set("aid", "no");
        RefreshAudioUi();

        if (_audioOrder.Count > 1 && _settings.ShowAudioMapOnOpen) ShowAudioMapDialog();

        InitPrincipalAudio();
        if (IsDisposed || _shuttingDown) return;
        if (_videoTracks.Count > 1 && _audioOrder.Count > 1 && _settings.ShowPrincipalAudioOnOpen) ShowPrincipalAudioDialog();

        // L'application a pu être fermée pendant qu'un dialogue était ouvert.
        if (IsDisposed || _shuttingDown || _master == null) return;

        if (_videoTracks.Count == 0 && _audioOrder.Count == 0)
        {
            MessageBox.Show(this, "Aucune piste lisible dans ce fichier.", "Grand Prix Player");
            return;
        }

        _fileReady = true;
        for (int i = 0; i < Math.Min(MaxViews, _videoTracks.Count); i++) CreateView(_videoTracks[i].Id);

        // Le maître (audio) ne démarre qu'une fois les vidéos prêtes, pour partir synchronisé.
        _pendingStart = true;
        _pendingSince = DateTime.UtcNow;
        Activate();
    }

    private List<TrackInfo> ApplyPreferredOrder(List<TrackInfo> audio)
    {
        var pref = _settings.AudioTitleOrder;
        if (!_settings.RememberAudioOrder || pref.Count == 0) return audio;
        int Rank(TrackInfo t)
        {
            if (string.IsNullOrWhiteSpace(t.Title)) return int.MaxValue;
            int i = pref.FindIndex(p => string.Equals(p, t.Title!.Trim(), StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }
        return audio.OrderBy(Rank).ToList(); // tri stable : les pistes inconnues gardent l'ordre du fichier
    }

    // =====================================================================
    //  Vues vidéo
    // =====================================================================

    private VideoView CreateView(int trackId)
    {
        var v = new VideoView { TrackId = trackId };
        v.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleFullscreen(v.FindForm() ?? this); };
        v.FileDropped += p => _ = OpenAsync(p);
        v.Clicked += () => SelectPrincipalAudio(v);
        var cms = Theme.Menu();
        cms.Opening += (_, _) => BuildViewMenu(cms, v);
        v.ContextMenuStrip = cms;

        int ordinal = _views.Count;
        _views.Add(v);

        if (_detached)
        {
            var saved = ordinal < _settings.DetachedWindows.Count ? _settings.DetachedWindows[ordinal] : null;
            var bounds = saved != null && saved.IsVisibleOnScreen() ? saved.ToRect() : DefaultDetachedBounds(ordinal);
            var w = new VideoWindow(this, v, bounds);
            _windows[v] = w;
            w.Show();
        }
        else
        {
            _grid.AddTile(v);
        }
        UpdateViewTitles();

        bool paused = _pendingStart || (_master?.GetFlag("pause") ?? true);
        v.StartPlayer(_file!, trackId, _master?.GetDouble("time-pos") ?? 0, paused);
        return v;
    }

    private Rectangle DefaultDetachedBounds(int i)
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int w = wa.Width / 2, h = wa.Height / 2;
        return new Rectangle(wa.X + (i % 2) * w, wa.Y + (i / 2 % 2) * h, w, h);
    }

    private void UpdateViewTitles()
    {
        foreach (var (v, w) in _windows)
        {
            int n = _views.IndexOf(v) + 1;
            var t = _videoTracks.FirstOrDefault(x => x.Id == v.TrackId);
            w.Text = $"Vidéo {n} — {t?.Label ?? "?"}";
        }
    }

    private async Task DestroyViewAsync(VideoView v)
    {
        if (!_views.Contains(v)) return;
        SaveDetachedBounds();
        _views.Remove(v);
        var p = v.Player;
        v.Player = null;
        // terminate_destroy hors du thread UI : la fenêtre mpv peut envoyer des messages à notre fenêtre.
        if (p != null) await Task.Run(p.Dispose);
        if (_windows.Remove(v, out var w)) w.CloseInternal();
        else _grid.RemoveTile(v);
        v.Dispose();
        UpdateViewTitles();
    }

    private async Task DestroyAllViewsAsync()
    {
        SaveDetachedBounds();
        var list = _views.ToList();
        _views.Clear();
        var players = list.Select(v => v.Player).OfType<MpvPlayer>().ToList();
        foreach (var v in list) v.Player = null;
        await Task.WhenAll(players.Select(p => Task.Run(p.Dispose)));
        foreach (var v in list)
        {
            if (_windows.Remove(v, out var w)) w.CloseInternal();
            else _grid.RemoveTile(v);
            v.Dispose();
        }
    }

    public void RequestRemoveView(VideoView v) => _ = DestroyViewAsync(v);

    private void AddView()
    {
        if (!_fileReady || _views.Count >= MaxViews || _videoTracks.Count == 0) return;
        var used = _views.Select(v => v.TrackId).ToHashSet();
        var t = _videoTracks.FirstOrDefault(x => !used.Contains(x.Id)) ?? _videoTracks[0];
        CreateView(t.Id);
    }

    private void ChangeViewTrack(VideoView v, int trackId)
    {
        v.TrackId = trackId;
        v.Player?.Set("vid", trackId.ToString(Inv));
        v.Player?.SeekAbsolute(_master?.GetDouble("time-pos") ?? _masterTime);
        v.CooldownUntil = DateTime.UtcNow.AddMilliseconds(800);
        UpdateViewTitles();
        var t = _videoTracks.FirstOrDefault(x => x.Id == trackId);
        v.Player?.ShowText(t?.Label ?? "");
    }

    private void BuildViewMenu(ContextMenuStrip cms, VideoView v)
    {
        cms.Items.Clear();
        var tracks = Theme.Item("Piste &vidéo");
        for (int i = 0; i < _videoTracks.Count; i++)
        {
            var t = _videoTracks[i];
            var mi = Theme.Item($"{i + 1}. {t.Label}", () => ChangeViewTrack(v, t.Id));
            mi.Checked = t.Id == v.TrackId;
            if (t.Details.Length > 0) mi.ShortcutKeyDisplayString = t.Details;
            tracks.DropDownItems.Add(mi);
        }
        cms.Items.Add(tracks);
        if (_audioOrder.Count > 0)
        {
            var audio = Theme.Item("Piste audio p&rincipale (clic)");
            int principal = _principalAudio.TryGetValue(v.TrackId, out var pa) ? pa : _audioOrder[0].Id;
            for (int i = 0; i < _audioOrder.Count; i++)
            {
                var a = _audioOrder[i];
                var mi = Theme.Item(a.Label, () => SetPrincipalAudio(v.TrackId, a.Id), i < 9 ? (i + 1).ToString(Inv) : null);
                mi.Checked = a.Id == principal;
                audio.DropDownItems.Add(mi);
            }
            audio.DropDownItems.Add(new ToolStripSeparator());
            audio.DropDownItems.Add(Theme.Item("Toutes les vidéos…", ShowPrincipalAudioDialog, "Ctrl+P"));
            cms.Items.Add(audio);
        }
        if (!_detached)
        {
            cms.Items.Add(Theme.Item("Mettre en vue &principale", () => _grid.PromoteToMain(v)));
            var g = Theme.Item("Disposition &grille", () => SetLayout(GridLayoutMode.Grid));
            g.Checked = _grid.Mode == GridLayoutMode.Grid;
            var m = Theme.Item("Disposition principale + &vignettes", () => SetLayout(GridLayoutMode.Main));
            m.Checked = _grid.Mode == GridLayoutMode.Main;
            cms.Items.Add(g);
            cms.Items.Add(m);
        }
        cms.Items.Add(new ToolStripSeparator());
        var add = Theme.Item("&Ajouter une vue", AddView, "Ctrl+N");
        add.Enabled = _views.Count < MaxViews;
        cms.Items.Add(add);
        cms.Items.Add(Theme.Item("Plein &écran", () => ToggleFullscreen(v.FindForm() ?? this), "F"));
        cms.Items.Add(Theme.Item("&Fermer cette vue", () => RequestRemoveView(v)));
    }

    // =====================================================================
    //  Modes d'affichage
    // =====================================================================

    private async Task SetDetachedAsync(bool detached)
    {
        if (_busy || detached == _detached) { UpdateMenuChecks(); return; }
        _busy = true;
        try
        {
            if (_fs) ToggleMainFullscreen();
            var tracks = _views.Select(v => v.TrackId).ToList();
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (_detached) _settings.MainDetached = WinBounds.From(b); else _settings.MainGlobal = WinBounds.From(b);
            await DestroyAllViewsAsync();
            _detached = detached;
            ApplyModeUi();
            if (_fileReady) foreach (var id in tracks) CreateView(id);
            Activate();
        }
        finally { _busy = false; }
    }

    private void ApplyModeUi()
    {
        _grid.Visible = !_detached;
        _audioList.Visible = _detached;
        var saved = _detached ? _settings.MainDetached : _settings.MainGlobal;
        Rectangle r;
        if (saved != null && saved.IsVisibleOnScreen()) r = saved.ToRect();
        else
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            var size = _detached ? new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(360))
                                 : new Size(Math.Min(wa.Width, LogicalToDeviceUnits(1280)), Math.Min(wa.Height, LogicalToDeviceUnits(800)));
            r = new Rectangle(wa.X + (wa.Width - size.Width) / 2, wa.Y + (wa.Height - size.Height) / 2, size.Width, size.Height);
            if (_detached) r.Y = wa.Bottom - size.Height - LogicalToDeviceUnits(40);
        }
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        Bounds = r;
        UpdateMenuChecks();
    }

    private void SetLayout(GridLayoutMode mode)
    {
        _grid.Mode = mode;
        _grid.LayoutTiles();
        UpdateMenuChecks();
    }

    private void UpdateMenuChecks()
    {
        _miDetached.Checked = _detached;
        _miGlobal.Checked = !_detached;
        _miGrid.Checked = _grid.Mode == GridLayoutMode.Grid;
        _miMain.Checked = _grid.Mode == GridLayoutMode.Main;
        _miGrid.Enabled = _miMain.Enabled = !_detached;
        _btnFs.Visible = !_detached;
        _showFsButton = !_detached;
        LayoutBar();
    }

    private void ToggleFullscreen(Form source)
    {
        if (source is VideoWindow w) w.ToggleFullscreen();
        else if (!_detached) ToggleMainFullscreen();
    }

    private bool ExitFullscreen(Form source)
    {
        if (source is VideoWindow { IsFullscreen: true } w) { w.ToggleFullscreen(); return true; }
        if (_fs) { ToggleMainFullscreen(); return true; }
        return false;
    }

    private void ToggleMainFullscreen()
    {
        if (!_fs)
        {
            _fsState = WindowState;
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            _fsRestore = Bounds;
            _menu.Visible = false;
            _bar.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            _fs = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            _menu.Visible = true;
            _bar.Visible = true;
            Bounds = _fsRestore;
            WindowState = _fsState;
            _fs = false;
        }
    }

    // =====================================================================
    //  Clavier
    // =====================================================================

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        HandleKey(keyData, this) || base.ProcessCmdKey(ref msg, keyData);

    /// <summary>Raccourcis communs à la fenêtre principale et aux fenêtres déportées.</summary>
    public bool HandleKey(Keys keyData, Form source)
    {
        var key = keyData & Keys.KeyCode;
        var mods = keyData & Keys.Modifiers;
        if (mods == Keys.None)
        {
            if (key is >= Keys.D1 and <= Keys.D9) { SelectAudioSlot(key - Keys.D0); return true; }
            if (key is >= Keys.NumPad1 and <= Keys.NumPad9) { SelectAudioSlot(key - Keys.NumPad0); return true; }
            switch (key)
            {
                case Keys.Space:
                case Keys.MediaPlayPause: TogglePause(); return true;
                case Keys.Left: SeekRelative(-10); return true;
                case Keys.Right: SeekRelative(10); return true;
                case Keys.Up: ChangeVolume(5); return true;
                case Keys.Down: ChangeVolume(-5); return true;
                case Keys.M: ToggleMute(); return true;
                case Keys.F:
                case Keys.F11: ToggleFullscreen(source); return true;
                case Keys.Escape: return ExitFullscreen(source);
                case Keys.F1: ShowHelp(); return true;
            }
        }
        else if (mods == Keys.Control)
        {
            switch (key)
            {
                case Keys.O: OpenDialog(); return true;
                case Keys.D: _ = SetDetachedAsync(!_detached); return true;
                case Keys.T: ShowAudioMapDialog(); return true;
                case Keys.P: ShowPrincipalAudioDialog(); return true;
                case Keys.N: AddView(); return true;
            }
        }
        return false;
    }

    private void ShowHelp() => MessageBox.Show(this,
        "Espace\tLecture / pause\n" +
        "← / →\tReculer / avancer de 10 s\n" +
        "↑ / ↓\tVolume + / −\n" +
        "M\tMuet\n" +
        "1 … 9\tPiste audio n° 1 à 9 (pavé numérique ou rangée du haut)\n" +
        "Ctrl+T\tModifier les numéros des pistes audio\n" +
        "Ctrl+P\tPistes audio principales des vidéos\n" +
        "Ctrl+D\tBasculer mode déporté / fenêtre unique\n" +
        "Ctrl+N\tAjouter une vue (4 max.)\n" +
        "F / F11\tPlein écran (fenêtre active)\n" +
        "Échap\tQuitter le plein écran\n" +
        "Ctrl+O\tOuvrir un fichier\n\n" +
        "Souris : clic sur une vidéo = sa piste audio principale ; double-clic = plein écran ;\n" +
        "clic droit sur une vidéo = choix de la piste vidéo et de sa piste audio principale ;\n" +
        "en fenêtre unique, glisser une vidéo sur une autre pour les échanger, et tirer les\n" +
        "séparateurs pour redimensionner.",
        "Raccourcis — Grand Prix Player");

    // =====================================================================
    //  Lecture
    // =====================================================================

    private void TogglePause()
    {
        if (_master == null || !_fileReady) return;
        if (_pendingStart) { StartPlayback(); return; }
        bool p = !_master.GetFlag("pause");
        _master.Set("pause", p ? "yes" : "no");
        foreach (var v in _views)
        {
            v.Player?.Set("pause", p ? "yes" : "no");
            v.Paused = p;
        }
        _btnPlay.Text = p ? GlyphPlay : GlyphPause;
    }

    private void StartPlayback()
    {
        _pendingStart = false;
        _master?.Set("pause", "no");
        foreach (var v in _views)
        {
            v.Player?.Set("pause", "no");
            v.Paused = false;
        }
    }

    private void SeekRelative(double delta)
    {
        if (_master == null || !_fileReady) return;
        double from = (DateTime.UtcNow - _lastSeek).TotalMilliseconds < 1500 ? _lastSeekTarget : _masterTime;
        SeekTo(from + delta);
    }

    private void SeekTo(double t)
    {
        if (_master == null || !_fileReady) return;
        if (_duration > 0) t = Math.Min(t, Math.Max(0, _duration - 0.5));
        t = Math.Max(0, t);
        _master.SeekAbsolute(t);
        foreach (var v in _views) v.Player?.SeekAbsolute(t);
        _lastSeek = DateTime.UtcNow;
        _lastSeekTarget = t;
        _masterTime = t;
        if (!_seek.Dragging) _seek.Value = t;
        _lblTime.Text = $"{Fmt(t)} / {Fmt(_duration)}";
    }

    private void ChangeVolume(int delta) => SetVolume(_settings.Volume + delta, true);

    private void SetVolume(int vol, bool osd)
    {
        vol = Math.Clamp(vol, 0, 100);
        _settings.Volume = vol;
        _master?.Set("volume", vol.ToString(Inv));
        if (!_vol.Dragging) _vol.Value = vol;
        _tips.SetToolTip(_vol, $"Volume {vol} %");
        if (osd) Osd($"Volume {vol} %");
    }

    private void ToggleMute()
    {
        _settings.Muted = !_settings.Muted;
        _master?.Set("mute", _settings.Muted ? "yes" : "no");
        UpdateMuteUi();
        Osd(_settings.Muted ? "Muet" : "Son activé");
    }

    private void UpdateMuteUi()
    {
        _btnMute.Text = _settings.Muted ? GlyphMute : GlyphVol;
        _vol.FillColor = _settings.Muted ? Theme.Muted : Theme.Text;
        _vol.Invalidate();
    }

    private void Osd(string text)
    {
        foreach (var v in _views) v.Player?.ShowText(text);
    }

    // =====================================================================
    //  Audio
    // =====================================================================

    private void SelectAudioSlot(int n, bool osd = true)
    {
        if (_master == null || n < 1 || n > _audioOrder.Count) return;
        var t = _audioOrder[n - 1];
        if (t.Id != _currentAid)
        {
            // Le maître n'a pas de vidéo : son horloge suit l'audio. Sans repositionnement explicite,
            // mpv reprend la nouvelle piste là où le cache de lecture anticipée s'est arrêté (saut de plusieurs secondes).
            bool wasSet = _currentAid > 0 && _fileReady;
            double pos = (DateTime.UtcNow - _lastSeek).TotalMilliseconds < 1500 ? _lastSeekTarget
                       : _master.GetDouble("time-pos") ?? _masterTime;
            _master.Set("aid", t.Id.ToString(Inv));
            _currentAid = t.Id;
            if (wasSet)
            {
                _master.SeekAbsolute(pos);
                _lastSeek = DateTime.UtcNow;
                _lastSeekTarget = pos;
            }
        }
        RefreshAudioUi();
        if (osd) Osd($"Audio {n} : {t.Label}");
    }

    private void ShowAudioMapDialog()
    {
        if (_audioOrder.Count == 0) return;
        using var dlg = new AudioMapDialog(_audioOrder, _currentAid, _settings.ShowAudioMapOnOpen, _settings.RememberAudioOrder);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _audioOrder = dlg.Result.ToList();
        _settings.ShowAudioMapOnOpen = dlg.ShowOnOpen;
        _settings.RememberAudioOrder = dlg.Remember;
        if (dlg.Remember)
        {
            var titles = _audioOrder.Where(t => !string.IsNullOrWhiteSpace(t.Title)).Select(t => t.Title!.Trim()).ToList();
            titles.AddRange(_settings.AudioTitleOrder.Where(o => !titles.Contains(o, StringComparer.OrdinalIgnoreCase)));
            _settings.AudioTitleOrder = titles;
        }
        _settings.Save();
        RefreshAudioUi();
    }

    // ---- Piste audio principale de chaque vidéo (activée par un clic sur la vidéo) ----

    /// <summary>Par défaut la piste n° 1, sauf choix mémorisé pour une vidéo du même nom.</summary>
    private void InitPrincipalAudio()
    {
        _principalAudio.Clear();
        if (_audioOrder.Count == 0) return;
        foreach (var v in _videoTracks)
        {
            int aid = _audioOrder[0].Id;
            if (_settings.RememberPrincipalAudio && !string.IsNullOrWhiteSpace(v.Title)
                && _settings.PrincipalAudioByVideoTitle.TryGetValue(v.Title!.Trim(), out var audioTitle))
            {
                var match = _audioOrder.FirstOrDefault(a => string.Equals(a.Title?.Trim(), audioTitle, StringComparison.OrdinalIgnoreCase));
                if (match != null) aid = match.Id;
            }
            _principalAudio[v.Id] = aid;
        }
    }

    private void ShowPrincipalAudioDialog()
    {
        if (_videoTracks.Count == 0 || _audioOrder.Count == 0) return;
        using var dlg = new PrincipalAudioDialog(_videoTracks, _audioOrder, _principalAudio,
            _settings.ShowPrincipalAudioOnOpen, _settings.RememberPrincipalAudio);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var (vid, aid) in dlg.Result) _principalAudio[vid] = aid;
        _settings.ShowPrincipalAudioOnOpen = dlg.ShowOnOpen;
        _settings.RememberPrincipalAudio = dlg.Remember;
        if (dlg.Remember) RememberPrincipalAudio();
        _settings.Save();
    }

    private void SetPrincipalAudio(int videoId, int audioId)
    {
        _principalAudio[videoId] = audioId;
        if (_settings.RememberPrincipalAudio) RememberPrincipalAudio();
        SelectAudioById(audioId);
    }

    private void RememberPrincipalAudio()
    {
        foreach (var (vid, aid) in _principalAudio)
        {
            var v = _videoTracks.FirstOrDefault(t => t.Id == vid);
            var a = _audioOrder.FirstOrDefault(t => t.Id == aid);
            if (string.IsNullOrWhiteSpace(v?.Title) || string.IsNullOrWhiteSpace(a?.Title)) continue;
            _settings.PrincipalAudioByVideoTitle[v!.Title!.Trim()] = a!.Title!.Trim();
        }
    }

    private void SelectPrincipalAudio(VideoView v)
    {
        if (!_fileReady || _audioOrder.Count == 0) return;
        SelectAudioById(_principalAudio.TryGetValue(v.TrackId, out var aid) ? aid : _audioOrder[0].Id);
    }

    private void SelectAudioById(int audioId)
    {
        int i = _audioOrder.FindIndex(t => t.Id == audioId);
        if (i >= 0) SelectAudioSlot(i + 1);
    }

    private void RefreshAudioUi()
    {
        // Menu Audio
        _mAudio.DropDownItems.Clear();
        for (int i = 0; i < _audioOrder.Count; i++)
        {
            var t = _audioOrder[i];
            int slot = i + 1;
            var mi = Theme.Item($"{t.Label}", () => SelectAudioSlot(slot), slot <= 9 ? slot.ToString(Inv) : null);
            mi.Checked = t.Id == _currentAid;
            mi.ToolTipText = t.Details;
            _mAudio.DropDownItems.Add(mi);
        }
        if (_audioOrder.Count > 0) _mAudio.DropDownItems.Add(new ToolStripSeparator());
        var cfg = Theme.Item("&Modifier les numéros des pistes…", ShowAudioMapDialog, "Ctrl+T");
        cfg.Enabled = _audioOrder.Count > 0;
        _mAudio.DropDownItems.Add(cfg);
        var principal = Theme.Item("Pistes audio &principales des vidéos…", ShowPrincipalAudioDialog, "Ctrl+P");
        principal.Enabled = _audioOrder.Count > 0 && _videoTracks.Count > 0;
        _mAudio.DropDownItems.Add(principal);

        // Bouton de la barre
        int cur = _audioOrder.FindIndex(t => t.Id == _currentAid);
        _btnAudio.Text = cur >= 0 ? $"♪  {cur + 1} · {_audioOrder[cur].Label}" : "♪  —";

        // Liste du mode déporté
        _audioList.BeginUpdate();
        _audioList.Items.Clear();
        foreach (var t in _audioOrder) _audioList.Items.Add(t);
        _audioList.EndUpdate();
        _audioList.Invalidate();
    }

    private void DrawAudioItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _audioOrder.Count) return;
        var t = _audioOrder[e.Index];
        bool current = t.Id == _currentAid;
        var g = e.Graphics;
        using (var b = new SolidBrush(current ? Theme.Accent : (e.Index % 2 == 0 ? Theme.Bg : Color.FromArgb(30, 30, 34)))) g.FillRectangle(b, e.Bounds);
        int pad = LogicalToDeviceUnits(14);
        var numRect = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, LogicalToDeviceUnits(36), e.Bounds.Height);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        using var bold = new Font(_audioList.Font, FontStyle.Bold);
        TextRenderer.DrawText(g, e.Index < 9 ? (e.Index + 1).ToString(Inv) : "·", bold, numRect, Color.White, flags);
        var textRect = Rectangle.FromLTRB(numRect.Right, e.Bounds.Y, e.Bounds.Right - pad, e.Bounds.Bottom);
        TextRenderer.DrawText(g, t.Label, _audioList.Font, textRect, Color.White, flags);
        using var small = new Font("Segoe UI", 9f);
        TextRenderer.DrawText(g, t.Details, small, textRect, current ? Color.White : Theme.Muted, flags | TextFormatFlags.Right);
    }

    private void ShowAudioPopup()
    {
        var cms = Theme.Menu();
        for (int i = 0; i < _audioOrder.Count; i++)
        {
            int slot = i + 1;
            var t = _audioOrder[i];
            var mi = Theme.Item(t.Label, () => SelectAudioSlot(slot), slot <= 9 ? slot.ToString(Inv) : null);
            mi.Checked = t.Id == _currentAid;
            cms.Items.Add(mi);
        }
        if (_audioOrder.Count > 0) cms.Items.Add(new ToolStripSeparator());
        cms.Items.Add(Theme.Item("Modifier les numéros…", ShowAudioMapDialog, "Ctrl+T"));
        cms.Closed += (_, _) => BeginInvoke(cms.Dispose);
        cms.Show(_btnAudio, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
    }

    // =====================================================================
    //  Boucle : événements mpv, interface, synchronisation
    // =====================================================================

    private void OnTick()
    {
        if (_master == null) return;
        _master.PumpEvents();
        foreach (var v in _views.ToArray()) v.Player?.PumpEvents();
        _tick++;
        if (_tick % 10 == 0) foreach (var v in _views) v.SuppressChildInput();
        if (!_fileReady) return;

        double mt = _master.GetDouble("time-pos") ?? _masterTime;
        bool paused = _master.GetFlag("pause");
        if ((DateTime.UtcNow - _lastSeek).TotalMilliseconds > 400) _masterTime = mt;
        if (_duration <= 0) _duration = _master.GetDouble("duration") ?? 0;

        if (!_seek.Dragging)
        {
            _seek.Maximum = _duration > 0 ? _duration : 1;
            _seek.Value = _masterTime;
            _lblTime.Text = $"{Fmt(_masterTime)} / {Fmt(_duration)}";
        }
        string glyph = paused ? GlyphPlay : GlyphPause;
        if (_btnPlay.Text != glyph) _btnPlay.Text = glyph;
        UpdateFullscreenBar(paused);

        if (_pendingStart)
        {
            bool ready = _views.All(v => v.Player?.FileLoaded == true);
            if (ready || (DateTime.UtcNow - _pendingSince).TotalSeconds > 5) StartPlayback();
            return;
        }

        if (_tick % 3 == 0) SyncViews(mt, paused);
    }

    /// <summary>
    /// En plein écran (fenêtre unique ou fenêtre déportée), affiche la barre de défilement flottante
    /// quand la souris arrive en bas de l'écran ; la masque peu après qu'elle en soit sortie.
    /// </summary>
    private void UpdateFullscreenBar(bool paused)
    {
        if (_fsBar?.IsDisposed == true) _fsBar = null; // détruite avec la fenêtre qui la possédait
        var pos = Cursor.Position;
        Form? host = _fs && Bounds.Contains(pos) ? this
                   : _windows.Values.FirstOrDefault(w => w.IsFullscreen && w.Bounds.Contains(pos));
        var bar = _fsBar;
        bool dragging = bar?.Seek.Dragging == true;
        var now = DateTime.UtcNow;

        if (host != null && bar != null && !dragging && bar.Owner != host)
        {
            // Nouvelle fenêtre plein écran : on recrée la barre pour qu'elle soit au-dessus de celle-ci.
            bar.Dispose();
            bar = _fsBar = null;
        }

        if (host != null)
        {
            bar ??= _fsBar = new FullscreenBar(() => SeekRelative(-10), TogglePause, () => SeekRelative(10), SeekTo);
            int h = bar.PreferredBarHeight(host);
            var r = host.Bounds;
            // Zone de déclenchement large (15 % de la hauteur) : sur un écran qui en a un autre en dessous,
            // la souris ne bute pas sur le bord et traverserait une zone étroite sans s'y arrêter.
            int zone = Math.Max(h, r.Height * 15 / 100);
            if (pos.Y >= r.Bottom - zone || dragging)
            {
                _fsBarLastNeeded = now;
                var rect = new Rectangle(r.X, r.Bottom - h, r.Width, h);
                if (bar.Owner != host) bar.Owner = host;
                if (bar.Bounds != rect) bar.Bounds = rect;
                if (!bar.Visible) bar.Show();
            }
        }

        if (bar is not { Visible: true }) return;
        if (!dragging && (now - _fsBarLastNeeded).TotalMilliseconds > 1200)
        {
            bar.Hide();
            return;
        }
        int cur = _audioOrder.FindIndex(t => t.Id == _currentAid);
        bar.UpdateState(_masterTime, _duration, paused, cur >= 0 ? $"♪  {cur + 1} · {_audioOrder[cur].Label}" : "");
    }

    private void SyncViews(double mt, bool paused)
    {
        var now = DateTime.UtcNow;
        bool recentSeek = (now - _lastSeek).TotalMilliseconds < 700;
        var log = _logFile != null ? new List<string>() : null;
        foreach (var v in _views)
        {
            var p = v.Player;
            if (p == null || !p.FileLoaded) continue;
            if (v.Paused != paused)
            {
                p.Set("pause", paused ? "yes" : "no");
                v.Paused = paused;
            }
            if (recentSeek || now < v.CooldownUntil) continue;
            var vt = p.GetDouble("time-pos");
            if (vt == null) continue;
            double d = vt.Value - mt;
            log?.Add(d.ToString("+0.000;-0.000", Inv));

            if (paused)
            {
                if (Math.Abs(d) > 0.1) { p.SeekAbsolute(mt); v.CooldownUntil = now.AddMilliseconds(500); }
                SetSpeed(v, 1);
            }
            else if (Math.Abs(d) > 0.75)
            {
                // Gros décalage : on repositionne avec une petite avance (le temps que la vidéo se recale).
                p.SeekAbsolute(mt + 0.15);
                SetSpeed(v, 1);
                v.CooldownUntil = now.AddMilliseconds(700);
            }
            else if (Math.Abs(d) > 0.035)
            {
                // Petit décalage : on accélère / ralentit légèrement la vidéo, sans à-coup.
                SetSpeed(v, Math.Clamp(1 - d, 0.85, 1.15));
            }
            else SetSpeed(v, 1);
        }
        if (log is { Count: > 0 } && _tick % 30 == 0)
        {
            try { File.AppendAllText(_logFile!, $"{DateTime.Now:HH:mm:ss.fff} t={mt:0.000} paused={paused} drift=[{string.Join(", ", log)}]\n"); }
            catch { }
        }
    }

    private static void SetSpeed(VideoView v, double s)
    {
        if (Math.Abs(v.Speed - s) < 0.005) return;
        v.Speed = s;
        v.Player?.Set("speed", s.ToString("0.000", CultureInfo.InvariantCulture));
    }

    // =====================================================================
    //  Divers UI
    // =====================================================================

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = OpenAsync(files[0]);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _audioList.ItemHeight = LogicalToDeviceUnits(34);
        LayoutBar();
    }

    private Button MakeButton(string text, string tip, Action click, bool icon)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            BackColor = Theme.Bar,
            TabStop = false,
            Font = icon ? new Font("Segoe MDL2 Assets", 11f) : new Font("Segoe UI", 9.5f, FontStyle.Bold),
            UseMnemonic = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.Hover;
        b.FlatAppearance.MouseDownBackColor = Theme.Track;
        b.Click += (_, _) => click();
        _tips.SetToolTip(b, tip);
        return b;
    }

    private void LayoutBar()
    {
        int S(int v) => LogicalToDeviceUnits(v);
        _bar.Height = S(72);
        int W = _bar.ClientSize.Width, pad = S(10);
        _seek.SetBounds(pad, S(6), Math.Max(10, W - 2 * pad), S(20));
        int y = S(32), h = S(32), x = pad;
        foreach (var b in new[] { _btnBack, _btnPlay, _btnFwd })
        {
            b.SetBounds(x, y, S(44), h);
            x += S(46);
        }
        int timeW = TextRenderer.MeasureText("0:00:00 / 0:00:00", _lblTime.Font).Width + S(8);
        _lblTime.SetBounds(x + S(4), y, timeW, h);
        x += timeW + S(8);

        int right = W - pad;
        if (_showFsButton) { _btnFs.SetBounds(right - S(36), y, S(36), h); right -= S(44); }
        int volW = W < S(640) ? S(70) : S(110);
        _vol.SetBounds(right - volW, y + (h - S(18)) / 2, volW, S(18));
        right -= volW + S(6);
        _btnMute.SetBounds(right - S(36), y, S(36), h);
        right -= S(44);
        _btnAudio.SetBounds(x, y, Math.Max(0, right - x), h);
    }

    private static string Fmt(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
