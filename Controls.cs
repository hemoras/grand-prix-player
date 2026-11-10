using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GrandPrixPlayer;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(24, 24, 27);
    public static readonly Color Bar = Color.FromArgb(36, 36, 40);
    public static readonly Color Hover = Color.FromArgb(62, 62, 68);
    public static readonly Color Track = Color.FromArgb(78, 78, 84);
    public static readonly Color Text = Color.FromArgb(228, 228, 231);
    public static readonly Color Muted = Color.FromArgb(150, 150, 158);
    public static readonly Color Accent = Color.FromArgb(225, 6, 0);

    public static readonly ToolStripProfessionalRenderer Renderer = new(new DarkColors());

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => Bar;
        public override Color MenuStripGradientEnd => Bar;
        public override Color ToolStripDropDownBackground => Bar;
        public override Color ImageMarginGradientBegin => Bar;
        public override Color ImageMarginGradientMiddle => Bar;
        public override Color ImageMarginGradientEnd => Bar;
        public override Color MenuBorder => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Track;
        public override Color SeparatorLight => Bar;
        public override Color CheckBackground => Accent;
        public override Color CheckSelectedBackground => Accent;
        public override Color CheckPressedBackground => Accent;
    }

    public static ToolStripMenuItem Item(string text, Action? onClick = null, string? keys = null)
    {
        var mi = new ToolStripMenuItem(text) { ForeColor = Text };
        if (keys != null) mi.ShortcutKeyDisplayString = keys;
        if (onClick != null) mi.Click += (_, _) => onClick();
        return mi;
    }

    public static ContextMenuStrip Menu() => new() { Renderer = Renderer, ShowImageMargin = true };
}

/// <summary>Barre de défilement / volume cliquable et glissable.</summary>
internal sealed class SeekBar : Control
{
    private double _value, _max = 1;

    public double Maximum { get => _max; set { _max = Math.Max(value, 0.001); _value = Math.Min(_value, _max); Invalidate(); } }
    public double Value { get => _value; set { _value = Math.Clamp(value, 0, _max); Invalidate(); } }
    public bool Dragging { get; private set; }
    public Color FillColor { get; set; } = Theme.Accent;

    /// <summary>Pendant le glissement.</summary>
    public event Action<double>? Scrubbing;
    /// <summary>Au relâchement de la souris.</summary>
    public event Action<double>? Committed;

    public SeekBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
        BackColor = Theme.Bar;
    }

    private double FromX(int x)
    {
        int pad = Height / 2;
        return Math.Clamp((x - pad) / (double)Math.Max(1, Width - 2 * pad), 0, 1) * _max;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Dragging = true;
        Capture = true;
        Value = FromX(e.X);
        Scrubbing?.Invoke(Value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!Dragging) return;
        Value = FromX(e.X);
        Scrubbing?.Invoke(Value);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!Dragging) return;
        Dragging = false;
        Capture = false;
        Committed?.Invoke(Value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = Height / 2;
        int th = Math.Max(4, Height / 4);
        int y = (Height - th) / 2;
        int w = Math.Max(1, Width - 2 * pad);
        int pos = pad + (int)(w * (_value / _max));
        using (var b = new SolidBrush(Theme.Track)) g.FillRectangle(b, pad, y, w, th);
        using (var b = new SolidBrush(FillColor)) g.FillRectangle(b, pad, y, pos - pad, th);
        int d = (int)(Height * 0.7);
        using (var b = new SolidBrush(Theme.Text)) g.FillEllipse(b, pos - d / 2, (Height - d) / 2, d, d);
    }
}

/// <summary>Surface qui héberge la sortie vidéo d'une instance mpv "esclave".</summary>
internal sealed class VideoView : Panel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string DragFormat = "GrandPrixPlayer.View";

    public MpvPlayer? Player { get; set; }
    public int TrackId { get; set; }
    public bool Paused { get; set; }
    public double Speed { get; set; } = 1;
    public DateTime CooldownUntil { get; set; }

    public event Action<string>? FileDropped;
    /// <summary>Clic gauche simple (appui + relâchement sans déplacement).</summary>
    public event Action? Clicked;

    private Point _down;
    private bool _pressed, _maybeDrag;

    public VideoView()
    {
        BackColor = Color.Black;
        AllowDrop = true;
        Cursor = Cursors.Arrow;
    }

    public void StartPlayer(string file, int vid, double start, bool paused)
    {
        var opts = new Dictionary<string, string>
        {
            ["wid"] = Handle.ToInt64().ToString(Inv),
            ["vid"] = vid.ToString(Inv),
            ["aid"] = "no",
            ["sid"] = "no",
            ["ao"] = "null",
            ["pause"] = paused ? "yes" : "no",
            ["start"] = Math.Max(0, start).ToString("0.###", Inv),
            ["keep-open"] = "yes",
            ["hwdec"] = "auto-safe",
            ["force-window"] = "yes",
            ["hr-seek"] = "yes",
            ["input-default-bindings"] = "no",
            ["input-vo-keyboard"] = "no",
            ["input-cursor"] = "no",
            ["cursor-autohide"] = "no",
            ["osc"] = "no",
            ["load-scripts"] = "no",
            ["ytdl"] = "no",
            ["terminal"] = "no",
            ["osd-level"] = "1",
            ["osd-font-size"] = "38",
            ["demuxer-max-bytes"] = "64MiB",
            ["demuxer-max-back-bytes"] = "32MiB",
        };
        Paused = paused;
        Speed = 1;
        Player = new MpvPlayer(opts);
        Player.Command("loadfile", file);
    }

    // --- La fenêtre créée par mpv est désactivée : souris et glisser-déposer arrivent alors sur ce panneau. ---
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enable);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);

    public void SuppressChildInput()
    {
        if (!IsHandleCreated || Player == null) return;
        EnumChildWindows(Handle, (h, _) =>
        {
            if (IsWindowEnabled(h)) EnableWindow(h, false);
            return true;
        }, IntPtr.Zero);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _down = e.Location;
        _pressed = true;
        _maybeDrag = Parent is VideoGrid; // échange de vidéos : seulement en mode fenêtre unique
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || e.Button != MouseButtons.Left) return;
        var ds = SystemInformation.DragSize;
        if (Math.Abs(e.X - _down.X) > ds.Width || Math.Abs(e.Y - _down.Y) > ds.Height)
        {
            // La souris a bougé : ce n'est plus un clic.
            _pressed = false;
            if (_maybeDrag)
            {
                _maybeDrag = false;
                DoDragDrop(new DataObject(DragFormat, this), DragDropEffects.Move);
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool click = _pressed && e.Button == MouseButtons.Left;
        _pressed = _maybeDrag = false;
        if (click) Clicked?.Invoke();
    }

    protected override void OnDragEnter(DragEventArgs e) => OnDragOver(e);

    protected override void OnDragOver(DragEventArgs e)
    {
        if (e.Data?.GetData(DragFormat) is VideoView src)
            e.Effect = src != this && src.Parent == Parent ? DragDropEffects.Move : DragDropEffects.None;
        else if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
        else
            e.Effect = DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(DragFormat) is VideoView src && src != this && Parent is VideoGrid grid)
            grid.Swap(src, this);
        else if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            FileDropped?.Invoke(files[0]);
    }
}

/// <summary>Conteneur du mode fenêtre unique : grille ou principale + vignettes, séparateurs déplaçables.</summary>
internal sealed class VideoGrid : Panel
{
    private enum Split { None, X, Y }

    public readonly List<VideoView> Tiles = new();
    public GridLayoutMode Mode { get; set; } = GridLayoutMode.Grid;
    public double GridX { get; set; } = 0.5;
    public double GridY { get; set; } = 0.5;
    public double MainX { get; set; } = 0.7;

    private Split _drag = Split.None;
    private int Gap => LogicalToDeviceUnits(5);

    public VideoGrid()
    {
        BackColor = Theme.Bg;
        DoubleBuffered = true;
    }

    public void AddTile(VideoView v)
    {
        Tiles.Add(v);
        Controls.Add(v);
        LayoutTiles();
    }

    public void RemoveTile(VideoView v)
    {
        Tiles.Remove(v);
        Controls.Remove(v);
        LayoutTiles();
    }

    public void Swap(VideoView a, VideoView b)
    {
        int i = Tiles.IndexOf(a), j = Tiles.IndexOf(b);
        if (i < 0 || j < 0) return;
        (Tiles[i], Tiles[j]) = (Tiles[j], Tiles[i]);
        LayoutTiles();
    }

    public void PromoteToMain(VideoView v)
    {
        if (Tiles.Count > 0 && Tiles[0] != v) Swap(Tiles[0], v);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutTiles();
    }

    private double XRatio => Mode == GridLayoutMode.Main ? MainX : GridX;

    public void LayoutTiles()
    {
        var rects = ComputeRects();
        for (int i = 0; i < Tiles.Count; i++) Tiles[i].Bounds = rects[i];
        Invalidate();
    }

    private Rectangle[] ComputeRects()
    {
        int n = Tiles.Count, W = ClientSize.Width, H = ClientSize.Height, g = Gap, h2 = g / 2;
        var r = new Rectangle[n];
        if (n == 0) return r;
        if (n == 1) { r[0] = new Rectangle(0, 0, W, H); return r; }

        int x = (int)(W * XRatio), y = (int)(H * GridY);
        static Rectangle E(int l, int t, int rr, int b) => Rectangle.FromLTRB(l, t, Math.Max(l + 1, rr), Math.Max(t + 1, b));

        if (Mode == GridLayoutMode.Main)
        {
            r[0] = E(0, 0, x - h2, H);
            int k = n - 1;
            int th = (H - (k - 1) * g) / k;
            for (int i = 0; i < k; i++)
                r[i + 1] = E(x + h2, i * (th + g), W, i == k - 1 ? H : i * (th + g) + th);
            return r;
        }

        switch (n)
        {
            case 2:
                r[0] = E(0, 0, x - h2, H);
                r[1] = E(x + h2, 0, W, H);
                break;
            case 3:
                r[0] = E(0, 0, x - h2, y - h2);
                r[1] = E(x + h2, 0, W, y - h2);
                r[2] = E(0, y + h2, W, H);
                break;
            default:
                r[0] = E(0, 0, x - h2, y - h2);
                r[1] = E(x + h2, 0, W, y - h2);
                r[2] = E(0, y + h2, x - h2, H);
                r[3] = E(x + h2, y + h2, W, H);
                break;
        }
        return r;
    }

    private Split HitSplit(Point p)
    {
        int n = Tiles.Count;
        if (n < 2) return Split.None;
        int x = (int)(ClientSize.Width * XRatio), y = (int)(ClientSize.Height * GridY), tol = Gap + 2;
        bool hasY = Mode == GridLayoutMode.Grid && n >= 3;
        if (hasY && Math.Abs(p.Y - y) <= tol) return Split.Y;
        if (Math.Abs(p.X - x) <= tol && !(hasY && n == 3 && p.Y > y)) return Split.X;
        return Split.None;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == Split.X)
        {
            double v = Math.Clamp(e.X / (double)Math.Max(1, ClientSize.Width), 0.15, 0.85);
            if (Mode == GridLayoutMode.Main) MainX = v; else GridX = v;
            LayoutTiles();
        }
        else if (_drag == Split.Y)
        {
            GridY = Math.Clamp(e.Y / (double)Math.Max(1, ClientSize.Height), 0.15, 0.85);
            LayoutTiles();
        }
        else
        {
            Cursor = HitSplit(e.Location) switch
            {
                Split.X => Cursors.VSplit,
                Split.Y => Cursors.HSplit,
                _ => Cursors.Default,
            };
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _drag = HitSplit(e.Location); Capture = _drag != Split.None; }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = Split.None;
        Capture = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Tiles.Count == 0)
        {
            TextRenderer.DrawText(e.Graphics,
                "Ouvrez un fichier (Ctrl+O) ou glissez-le ici",
                Font, ClientRectangle, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

/// <summary>Fenêtre indépendante du mode déporté.</summary>
internal sealed class VideoWindow : Form
{
    private readonly MainForm _main;
    private bool _internalClose, _fs;
    private Rectangle _restore;
    private FormWindowState _restoreState;

    public VideoView View { get; }
    public bool IsFullscreen => _fs;
    public Rectangle NormalBounds => _fs ? _restore : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

    public VideoWindow(MainForm main, VideoView view, Rectangle bounds)
    {
        _main = main;
        View = view;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        Icon = main.Icon;
        view.Dock = DockStyle.Fill;
        Controls.Add(view);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        _main.HandleKey(keyData, this) || base.ProcessCmdKey(ref msg, keyData);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_internalClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _main.RequestRemoveView(View);
            return;
        }
        base.OnFormClosing(e);
    }

    public void CloseInternal()
    {
        _internalClose = true;
        Close();
    }

    public void ToggleFullscreen()
    {
        if (!_fs)
        {
            _restoreState = WindowState;
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            _restore = Bounds;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            _fs = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _restore;
            WindowState = _restoreState;
            _fs = false;
        }
    }
}

/// <summary>Attribution des numéros 1 à 9 aux pistes audio.</summary>
internal sealed class AudioMapDialog : Form
{
    private readonly List<TrackInfo> _order;
    private readonly int _currentAid;
    private readonly ListView _lv = new();
    private readonly CheckBox _chkShow = new();
    private readonly CheckBox _chkRemember = new();

    public IReadOnlyList<TrackInfo> Result => _order;
    public bool ShowOnOpen => _chkShow.Checked;
    public bool Remember => _chkRemember.Checked;

    public AudioMapDialog(IEnumerable<TrackInfo> tracks, int currentAid, bool showOnOpen, bool remember)
    {
        _order = tracks.ToList();
        _currentAid = currentAid;

        Text = "Numéros des pistes audio";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = new Size(600, 400);

        var info = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 8),
            Text = "Sélectionnez une piste puis tapez le numéro (1 à 9) à lui donner : " +
                   "les deux pistes échangent leur numéro. En lecture, ce numéro (pavé numérique ou rangée du haut) " +
                   "bascule instantanément sur la piste.",
            MaximumSize = new Size(570, 0),
        };

        _lv.Dock = DockStyle.Fill;
        _lv.View = View.Details;
        _lv.FullRowSelect = true;
        _lv.HideSelection = false;
        _lv.MultiSelect = false;
        _lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _lv.Columns.Add("N°", 50);
        _lv.Columns.Add("Piste", 240);
        _lv.Columns.Add("Détails", 260);

        _chkShow.Text = "Afficher cette fenêtre à chaque ouverture de fichier";
        _chkShow.Checked = showOnOpen;
        _chkShow.AutoSize = true;
        _chkRemember.Text = "Mémoriser l'ordre (par nom de piste) pour les prochains fichiers";
        _chkRemember.Checked = remember;
        _chkRemember.AutoSize = true;

        var up = new Button { Text = "▲ Monter", AutoSize = true };
        var down = new Button { Text = "▼ Descendre", AutoSize = true };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 0) };
        var cancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 0) };
        up.Click += (_, _) => MoveSelection(-1);
        down.Click += (_, _) => MoveSelection(+1);
        AcceptButton = ok;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange(new Control[] { cancel, ok, down, up });

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(info, 0, 0);
        table.Controls.Add(_lv, 0, 1);
        table.Controls.Add(_chkShow, 0, 2);
        table.Controls.Add(_chkRemember, 0, 3);
        table.Controls.Add(buttons, 0, 4);
        Controls.Add(table);

        Refill(_order.FindIndex(t => t.Id == currentAid) is var i and >= 0 ? i : 0);
    }

    private void Refill(int select)
    {
        _lv.BeginUpdate();
        _lv.Items.Clear();
        for (int i = 0; i < _order.Count; i++)
        {
            var t = _order[i];
            var item = new ListViewItem(i < 9 ? (i + 1).ToString() : "—");
            item.SubItems.Add(t.Label + (t.Id == _currentAid ? "   ◀ en cours" : ""));
            item.SubItems.Add(t.Details);
            _lv.Items.Add(item);
        }
        _lv.EndUpdate();
        if (select >= 0 && select < _lv.Items.Count)
        {
            _lv.Items[select].Selected = true;
            _lv.Items[select].Focused = true;
            _lv.EnsureVisible(select);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = _lv;
        _lv.Focus();
    }

    private int Selected => _lv.SelectedIndices.Count > 0 ? _lv.SelectedIndices[0] : -1;

    private void Assign(int number)
    {
        int src = Selected, dst = number - 1;
        if (src < 0 || dst >= _order.Count) { System.Media.SystemSounds.Beep.Play(); return; }
        (_order[src], _order[dst]) = (_order[dst], _order[src]);
        Refill(dst);
    }

    private void MoveSelection(int delta)
    {
        int src = Selected, dst = src + delta;
        if (src < 0 || dst < 0 || dst >= _order.Count) return;
        (_order[src], _order[dst]) = (_order[dst], _order[src]);
        Refill(dst);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int n = e.KeyCode is >= Keys.D1 and <= Keys.D9 ? e.KeyCode - Keys.D0
              : e.KeyCode is >= Keys.NumPad1 and <= Keys.NumPad9 ? e.KeyCode - Keys.NumPad0 : 0;
        if (n > 0 && e.Modifiers == Keys.None)
        {
            Assign(n);
            e.Handled = e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }
}

/// <summary>Choix, pour chaque piste vidéo, de la piste audio activée quand on clique sur cette vidéo.</summary>
internal sealed class PrincipalAudioDialog : Form
{
    private sealed record AudioChoice(TrackInfo Track, int Number)
    {
        public override string ToString() => Number <= 9 ? $"{Number}  ·  {Track.Label}" : $"—  ·  {Track.Label}";
    }

    private readonly List<(TrackInfo Video, ComboBox Combo)> _rows = new();
    private readonly CheckBox _chkShow = new();
    private readonly CheckBox _chkRemember = new();

    public bool ShowOnOpen => _chkShow.Checked;
    public bool Remember => _chkRemember.Checked;

    /// <summary>Id de piste vidéo → Id de piste audio.</summary>
    public Dictionary<int, int> Result => _rows.ToDictionary(r => r.Video.Id, r => ((AudioChoice)r.Combo.SelectedItem!).Track.Id);

    public PrincipalAudioDialog(IReadOnlyList<TrackInfo> videos, IReadOnlyList<TrackInfo> audioOrder,
                                IReadOnlyDictionary<int, int> current, bool showOnOpen, bool remember)
    {
        Text = "Choix des pistes audio principales";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var info = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            Padding = new Padding(0, 0, 0, 10),
            Text = "Pour chaque piste vidéo, choisissez la piste audio à écouter quand vous cliquez sur cette vidéo. " +
                   "Les touches 1 à 9 restent disponibles à tout moment.",
        };

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        var choices = audioOrder.Select((t, i) => new AudioChoice(t, i + 1)).ToArray();
        for (int i = 0; i < videos.Count; i++)
        {
            var v = videos[i];
            var lbl = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Text = $"Vidéo {i + 1} — {v.Label}",
                Margin = new Padding(0, 6, 12, 6),
            };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
            combo.Items.AddRange(choices);
            int sel = current.TryGetValue(v.Id, out var aid) ? Array.FindIndex(choices, c => c.Track.Id == aid) : 0;
            combo.SelectedIndex = Math.Max(0, sel);
            grid.Controls.Add(lbl, 0, i);
            grid.Controls.Add(combo, 1, i);
            _rows.Add((v, combo));
        }

        _chkShow.Text = "Afficher cette fenêtre à chaque ouverture de fichier";
        _chkShow.Checked = showOnOpen;
        _chkShow.AutoSize = true;
        _chkShow.Margin = new Padding(0, 10, 0, 0);
        _chkRemember.Text = "Mémoriser ces choix (par nom de piste) pour les prochains fichiers";
        _chkRemember.Checked = remember;
        _chkRemember.AutoSize = true;
        _chkRemember.Margin = new Padding(0, 3, 0, 0);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 0) };
        var cancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 0) };
        AcceptButton = ok;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange(new Control[] { cancel, ok });

        var table = new TableLayoutPanel { AutoSize = true, Padding = new Padding(14), ColumnCount = 1, Dock = DockStyle.Fill };
        table.Controls.Add(info);
        table.Controls.Add(grid);
        table.Controls.Add(_chkShow);
        table.Controls.Add(_chkRemember);
        table.Controls.Add(buttons);
        Controls.Add(table);
    }
}

/// <summary>
/// Barre de contrôle flottante du plein écran. C'est une petite fenêtre à part (possédée par la fenêtre plein écran)
/// pour être sûre de s'afficher au-dessus du rendu vidéo, et elle ne prend jamais le focus clavier.
/// </summary>
internal sealed class FullscreenBar : Form
{
    private const string GlyphPlay = "", GlyphPause = "";
    public readonly SeekBar Seek = new();
    private readonly Label _time = new(), _audio = new();
    private readonly Button _back, _play, _fwd;
    private double _duration;

    public FullscreenBar(Action back, Action playPause, Action fwd, Action<double> seek)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        BackColor = Theme.Bar;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10f);

        _back = MakeButton("−10", false, back);
        _play = MakeButton(GlyphPause, true, playPause);
        _fwd = MakeButton("+10", false, fwd);
        _time.TextAlign = _audio.TextAlign = ContentAlignment.MiddleLeft;
        _audio.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _audio.AutoEllipsis = true;
        Seek.Committed += seek;
        Seek.Scrubbing += v => _time.Text = $"{Fmt(v)} / {Fmt(_duration)}";
        Controls.AddRange(new Control[] { Seek, _back, _play, _fwd, _time, _audio });
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x80 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }

    public int PreferredBarHeight(Control host) => host.LogicalToDeviceUnits(76);

    public void UpdateState(double t, double duration, bool paused, string audio)
    {
        _duration = duration;
        if (!Seek.Dragging)
        {
            Seek.Maximum = duration > 0 ? duration : 1;
            Seek.Value = t;
            _time.Text = $"{Fmt(t)} / {Fmt(duration)}";
        }
        var g = paused ? GlyphPlay : GlyphPause;
        if (_play.Text != g) _play.Text = g;
        if (_audio.Text != audio) _audio.Text = audio;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int S(int v) => LogicalToDeviceUnits(v);
        int W = ClientSize.Width, pad = S(24);
        Seek.SetBounds(pad, S(8), Math.Max(10, W - 2 * pad), S(22));
        int y = S(36), h = S(32), x = pad;
        foreach (var b in new[] { _back, _play, _fwd }) { b.SetBounds(x, y, S(44), h); x += S(46); }
        int timeW = TextRenderer.MeasureText("0:00:00 / 0:00:00", _time.Font).Width + S(8);
        _time.SetBounds(x + S(4), y, timeW, h);
        x += timeW + S(12);
        _audio.SetBounds(x, y, Math.Max(0, W - pad - x), h);
    }

    private static Button MakeButton(string text, bool icon, Action click)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            BackColor = Theme.Bar,
            TabStop = false,
            UseMnemonic = false,
            Font = icon ? new Font("Segoe MDL2 Assets", 11f) : new Font("Segoe UI", 9.5f, FontStyle.Bold),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.Hover;
        b.FlatAppearance.MouseDownBackColor = Theme.Track;
        b.Click += (_, _) => click();
        return b;
    }

    private static string Fmt(double s)
    {
        if (double.IsNaN(s) || s < 0) s = 0;
        var t = TimeSpan.FromSeconds(s);
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
