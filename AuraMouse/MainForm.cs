using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using AuraMouse.Remote;
using AuraMouse.Transfers;
using AuraMouse.Ui;

namespace AuraMouse;

/// The whole window. Each page is rebuilt from the state fields below, so switching
/// language just re-renders the current page without touching the running session.
internal sealed class MainForm : Form
{
    enum Page { Choose, Host, Client }
    enum Section { Connection, Remote, Computers, Settings } // the sidebar; Connection shows the current Page

    const float SidebarWidth = 210, DesignWidth = 540, WindowWidth = SidebarWidth + DesignWidth, WindowHeight = 690;
    const float Pad = 32, ContentWidth = DesignWidth - 2 * Pad;
    const float PageTop = 56; // the pages' coordinates were laid out with a header above them, now in the sidebar

    Page _page = Page.Choose;
    HostSession? _host;
    ClientSession? _client;

    // Page state that survives a language switch. Status text is a function so it re-reads the language.
    Func<string> _statusText = () => "";
    Tone _statusTone;
    const int MaxDevices = 32;
    int _pairingCode;
    IReadOnlyList<PeerInfo> _peers = [];
    bool _clientConnected, _connecting;
    readonly List<Device> _devices = [];
    string _typedCode = "";
    SavedComputer? _savedHost; // connecting with a saved key instead of the code
    string? _firewallKey; // what to tell the user about the firewall while nothing is connected
    bool _startHidden, _resumeLast, _exiting, _trayHintShown, _wakeChecking;
    Section _section = Section.Connection;
    readonly List<(WakeTarget Target, TextBlock Status, PillButton Button)> _wakeRows = [];
    readonly Dictionary<string, DateTime> _wakeSent = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _wakeUp = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Windows.Forms.Timer _wakeTimer = new() { Interval = 4000 };
    bool _wakeNoNetwork;
    readonly NotifyIcon _tray;
    const int MaxRemoteRows = 4, MaxRemoteTrustedRows = 3, MaxComputerRows = 6;
    RemoteHost? _remote;
    RemoteBanner? _remoteBanner;
    List<(string Name, IPAddress Ip)> _remoteFound = [];
    bool _remoteSearching, _remoteSearched, _remoteFirewallChecked;
    string _remoteAddress = "";

    // Live controls of the current page (null when that page isn't showing).
    StatusPill? _status;
    LayoutMap? _layoutMap;
    PillButton? _sendButton;
    TransfersWindow? _transfers;
    CodeDisplay? _codeDisplay;
    TextBlock? _addAnother;
    PillButton? _connectButton;
    CodeInput? _codeInput;
    Bitmap? _backdrop;

    public MainForm(bool startHidden = false, bool resumeLast = false)
    {
        _startHidden = startHidden;
        _resumeLast = resumeLast;
        L.Load();
        Text = "Aura Mouse";
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Background;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        Theme.Scale = DeviceDpi / 96f;
        Icon = Theme.CreateAppIcon();
        AllowDrop = true;
        DragOver += OnDragOver;
        DragDrop += OnDragDrop;
        TransferCenter.Changed += () => RunOnUi(() =>
        {
            _transfers ??= new TransfersWindow();
            _transfers.ShowNearCorner(this);
        });
        _tray = CreateTray();
        _wakeTimer.Tick += (_, _) => CheckWakeTargets();
        _wakeTimer.Start();
        Render();
    }

    // ───────────────────────── tray ─────────────────────────

    NotifyIcon CreateTray()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var tray = new NotifyIcon { Icon = Icon, Text = "Aura Mouse", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => BringBack();

        // Built when opened, so it follows the language and whether the mouse is locked.
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            menu.RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No;
            menu.Items.Add(L.T("tray_open"), null, (_, _) => BringBack());
            if (_host != null || _client != null)
                menu.Items.Add(L.T(IsLocked ? "unlocked_notice" : "lock_mouse"), null, (_, _) => ToggleLock());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("tray_exit"), null, (_, _) => ExitApp());
        };
        return tray;
    }

    /// Any thread.
    public void BringBack() => RunOnUi(() =>
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    });

    void ExitApp()
    {
        _exiting = true;
        StopRemote();
        Close();
    }

    bool IsLocked => _host?.Locked ?? _client?.Locked ?? false;

    void ToggleLock()
    {
        _host?.ToggleLock();
        _client?.ToggleLock();
    }

    /// Started with Windows: nothing on screen, just the tray icon, but everything else runs as usual.
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            _startHidden = false;
            value = false;
            if (!IsHandleCreated) CreateHandle();
            BeginInvoke(ResumeLast);
        }
        base.SetVisibleCore(value);
    }

    /// Goes straight back to what this computer was doing last time.
    void ResumeLast()
    {
        if (!_resumeLast) return;
        _resumeLast = false;
        if (PairStore.Role == "host") StartHost();
        else if (PairStore.Role == "client" && PairStore.Host is { } saved) StartClient(saved);
    }

    // ───────────────────────── pages ─────────────────────────

    void Render()
    {
        SuspendLayout();
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        old.ForEach(c => c.Dispose());
        _status = null; _layoutMap = null; _sendButton = null; _codeDisplay = null; _addAnother = null;
        _connectButton = null; _codeInput = null;

        ClientSize = new Size(S(WindowWidth), S(WindowHeight));
        RenderSidebar();
        _wakeRows.Clear();
        if (_section == Section.Computers) RenderComputers();
        else if (_section == Section.Settings) RenderSettings();
        else if (_section == Section.Remote) RenderRemote();
        else switch (_page)
        {
            case Page.Choose: RenderChoose(); break;
            case Page.Host: RenderHost(); break;
            case Page.Client: RenderClient(); break;
        }
        ResumeLayout();
        Invalidate();
    }

    void RenderSidebar()
    {
        const float x = 12, w = SidebarWidth - 2 * x;
        AddToSidebar(new BrandMark(), 20, 20, w - 8, 40);

        float y = 84;
        foreach (var (section, key, glyph) in new[]
        {
            (Section.Connection, "nav_connection", Glyphs.Link),
            (Section.Remote, "nav_remote", Glyphs.Monitor),
            (Section.Computers, "nav_computers", Glyphs.Computers),
            (Section.Settings, "nav_settings", Glyphs.Settings),
        })
        {
            var item = AddToSidebar(new SidebarItem { Text = L.T(key), Glyph = glyph, Selected = _section == section }, x, y, w, 46);
            item.Click += (_, _) => ShowSection(section);
            y += 52;
        }

        // From the bottom up. The language is here, not in the settings, so it can be found without reading anything.
        var language = AddToSidebar(new PillButton { Text = L.Current.Name, Glyph = Glyphs.Globe }, x, WindowHeight - 60, w, 42);
        language.Click += (_, _) => ChooseLanguage();

        if (_host != null || _client != null)
        {
            bool locked = IsLocked;
            var lockButton = AddToSidebar(new PillButton { Text = L.T(locked ? "mouse_locked" : "lock_mouse"), Glyph = Glyphs.Lock, Kind = locked ? ButtonKind.Primary : ButtonKind.Ghost },
                x, WindowHeight - 110, w, 42);
            lockButton.Click += (_, _) => ToggleLock();
        }
    }

    void ChooseLanguage()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        foreach (var language in L.Languages)
        {
            var code = language.Code;
            var item = menu.Items.Add(language.Name, null, (_, _) =>
            {
                L.Set(code);
                L.Save();
                _backdrop?.Dispose();
                _backdrop = null; // the sidebar may change sides
                Navigate();
            });
            item.Font = new Font(language.Font, item.Font.Size, code == L.Current.Code ? FontStyle.Bold : FontStyle.Regular);
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(Cursor.Position);
    }

    void ShowSection(Section section)
    {
        _section = section;
        Navigate();
    }

    /// Everything known about a computer in one row: whether it's connected, turning it on, and removing it.
    void RenderComputers()
    {
        AddCentered(new TextBlock { Text = L.T("computers_title"), FontSize = 18, Bold = true }, 84, ContentWidth, 40);
        AddCentered(new TextBlock { Text = L.T("computers_sub"), FontSize = 10, Color = Theme.Muted }, 124, ContentWidth, 26);

        var clients = PairStore.Clients;
        var savedHost = PairStore.Host;
        var targets = PairStore.WakeTargets;
        var names = clients.Select(c => c.Name).Concat(savedHost == null ? [] : [savedHost.Name]).Concat(targets.Select(t => t.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxComputerRows).ToList();

        if (names.Count == 0)
            AddCentered(new TextBlock { Text = L.T("computers_empty"), FontSize = 11 }, 230, ContentWidth, 40);

        const float wakeWidth = 112, removeWidth = 42;
        float textWidth = ContentWidth - wakeWidth - removeWidth - 20, y = 170;
        var align = L.Rtl ? StringAlignment.Far : StringAlignment.Near;
        foreach (var name in names)
        {
            bool same(string other) => string.Equals(other, name, StringComparison.OrdinalIgnoreCase);
            var client = clients.FirstOrDefault(c => same(c.Name));
            bool isHost = savedHost != null && same(savedHost.Name);
            var target = targets.FirstOrDefault(t => same(t.Name));
            bool connected = _peers.Any(p => same(p.Name)) || isHost && _clientConnected;

            Add(new TextBlock { Text = name, FontSize = 11.5f, Bold = true, Align = align }, Pad, y, textWidth, 28);
            var status = Add(new TextBlock { FontSize = 9.5f, Color = connected ? Theme.Good : Theme.Muted, Align = align }, Pad, y + 28, textWidth, 34);
            status.Text = L.T(connected ? "computer_connected" : client != null ? "computer_is_client" : "computer_is_host");
            if (target != null && !connected)
            {
                var wake = Add(new PillButton { Text = L.T("wake_button"), Glyph = Glyphs.Power, Kind = ButtonKind.Primary }, DesignWidth - Pad - removeWidth - 8 - wakeWidth, y + 6, wakeWidth, 42);
                wake.Click += (_, _) => WakeUp(target);
                _wakeRows.Add((target, status, wake));
            }
            var remove = Add(new PillButton { Glyph = Glyphs.Delete, Kind = ButtonKind.Danger }, DesignWidth - Pad - removeWidth, y + 6, removeWidth, 42);
            remove.Click += (_, _) =>
            {
                if (client != null)
                {
                    PairStore.RemoveClient(client.Id);
                    _host?.Disconnect(client.Id);
                }
                if (isHost)
                {
                    PairStore.RemoveHost();
                    if (_client != null && _savedHost?.Id == savedHost!.Id)
                    {
                        var section = _section;
                        StopAll();
                        _section = section;
                    }
                }
                PairStore.RemoveWake(name);
                Navigate();
            };
            y += 68;
        }

        AddCentered(new TextBlock { Text = L.T("computers_add"), FontSize = 9.5f, Color = Theme.Muted }, 586, ContentWidth, 26);
        if (targets.Count > 0)
            AddCentered(new TextBlock { Text = L.T("wake_needs"), FontSize = 9, Color = Theme.Muted }, 612, ContentWidth, 56);
        if (PairStore.HasPairs)
        {
            var forget = AddCentered(new PillButton { Text = L.T("forget"), Glyph = Glyphs.Delete }, 676, 200, 42);
            forget.Click += (_, _) =>
            {
                if (MessageBox.Show(this, L.T("forget_confirm"), "Aura Mouse", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                StopAll();
                PairStore.Forget();
                _section = Section.Computers;
                Navigate();
            };
        }
        UpdateWakeRows();
        CheckWakeTargets();
    }

    // ───────────────────────── remote desktop ─────────────────────────

    /// Kept apart from everything else on this window: nothing here depends on the mouse sharing or touches it.
    public void StartRemote()
    {
        if (!RemoteStore.Enabled || _remote != null) return;
        var remote = new RemoteHost(AskRemote);
        remote.ViewerChanged += viewer => RunOnUi(() => ShowRemoteBanner(viewer));
        try { remote.Start(); }
        catch (SocketException)
        {
            remote.Dispose();
            return;
        }
        _remote = remote;
    }

    void StopRemote()
    {
        _remote?.EndSession();
        _remote?.Dispose();
        _remote = null;
    }

    /// Asked from the network's thread; the question goes on screen on the window's.
    Task<RemoteAnswer> AskRemote(RemoteRequest request, CancellationToken ct)
    {
        var result = new TaskCompletionSource<RemoteAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUi(() =>
        {
            var form = new RemoteApprovalForm(request);
            form.Answer.ContinueWith(t =>
            {
                if (t.IsCanceled) result.TrySetCanceled();
                else result.TrySetResult(t.Result);
            }, TaskScheduler.Default);
            ct.Register(() => RunOnUi(() => { if (!form.IsDisposed) form.Withdraw(); }));
            form.Show();
        });
        return result.Task;
    }

    void ShowRemoteBanner(string? viewer)
    {
        _remoteBanner?.Close();
        _remoteBanner = null;
        if (viewer != null)
        {
            _remoteBanner = new RemoteBanner(viewer, () => _remote?.EndSession());
            _remoteBanner.Show();
        }
        if (_section == Section.Remote) Navigate();
    }

    void RenderRemote()
    {
        AddCentered(new TextBlock { Text = L.T("remote_title"), FontSize = 18, Bold = true }, 76, ContentWidth, 38);
        AddCentered(new TextBlock { Text = L.T("remote_sub"), FontSize = 10, Color = Theme.Muted }, 112, ContentWidth, 24);
        var align = L.Rtl ? StringAlignment.Far : StringAlignment.Near;

        // Computers: found now, or viewed before.
        Add(new TextBlock { Text = L.T("remote_nearby"), FontSize = 9.5f, Bold = true, Color = Theme.Muted, Align = align }, Pad, 150, ContentWidth - 130, 24);
        var refresh = Add(new PillButton { Text = L.T(_remoteSearching ? "remote_searching" : "remote_refresh") }, DesignWidth - Pad - 120, 146, 120, 32);
        refresh.Enabled = !_remoteSearching;
        refresh.Click += (_, _) => SearchRemote();

        bool isMe(RemoteComputer c) => c.Name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) || System.Net.IPAddress.TryParse(c.Ip, out var a) && LanScan.IsThisComputer(a);
        var saved = RemoteStore.Computers.Where(c => !isMe(c)).ToList();
        var rows = _remoteFound.Select(f => (f.Name, Ip: f.Ip.ToString(), Online: true, Saved: saved.FirstOrDefault(c => c.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase))))
            .Concat(saved.Where(c => !_remoteFound.Any(f => f.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(c => (c.Name, c.Ip, Online: false, Saved: (RemoteComputer?)c)))
            .Take(MaxRemoteRows).ToList();
        float y = 184;
        if (rows.Count == 0)
            AddCentered(new TextBlock { Text = L.T(_remoteSearching ? "remote_searching" : "remote_none"), FontSize = 10, Color = Theme.Muted }, y + 10, ContentWidth, 44);
        const float viewWidth = 96, forgetWidth = 40;
        foreach (var row in rows)
        {
            float textWidth = ContentWidth - viewWidth - forgetWidth - 16;
            Add(new TextBlock { Text = row.Name, FontSize = 11, Bold = true, Align = align }, Pad, y, textWidth, 24);
            var detail = row.Ip + "  ·  " + L.T(row.Saved?.Key != null ? "remote_auto" : row.Online ? "remote_online" : "remote_offline");
            Add(new TextBlock { Text = detail, FontSize = 9, Color = row.Saved?.Key != null ? Theme.Good : Theme.Muted, Align = align }, Pad, y + 24, textWidth, 20);
            var view = Add(new PillButton { Text = L.T("remote_view"), Glyph = Glyphs.Monitor, Kind = ButtonKind.Primary }, DesignWidth - Pad - viewWidth - (row.Saved != null ? forgetWidth + 8 : 0), y + 3, viewWidth, 38);
            var name = row.Name;
            var ip = row.Ip;
            var known = row.Saved;
            view.Click += (_, _) => OpenViewer(System.Net.IPAddress.Parse(ip), name, known);
            if (known != null)
            {
                var forget = Add(new PillButton { Glyph = Glyphs.Delete }, DesignWidth - Pad - forgetWidth, y + 3, forgetWidth, 38);
                forget.Click += (_, _) =>
                {
                    RemoteStore.ForgetComputer(name);
                    Navigate();
                };
            }
            y += 50;
        }

        // Connecting by address, for what the search can't see.
        const float goWidth = 110;
        var address = Add(new TextBox
        {
            Font = Theme.RealFont(11),
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = L.T("remote_address"),
            RightToLeft = RightToLeft.No,
            Text = _remoteAddress, // the page is rebuilt when a search finishes, perhaps mid-typing
        }, Pad, 392, ContentWidth - goWidth - 10, 36);
        address.TextChanged += (_, _) => _remoteAddress = address.Text;
        if (_remoteAddress.Length > 0)
        {
            address.SelectionStart = _remoteAddress.Length;
            BeginInvoke(() => { if (!address.IsDisposed) address.Focus(); });
        }
        var go = Add(new PillButton { Text = L.T("remote_connect"), Glyph = Glyphs.Link, Kind = ButtonKind.Primary }, DesignWidth - Pad - goWidth, 388, goWidth, 42);
        go.Click += async (_, _) => await ViewAddress(address.Text.Trim());
        address.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await ViewAddress(address.Text.Trim());
        };

        // Letting others ask.
        bool on = RemoteStore.Enabled;
        const float toggleWidth = 180;
        Add(new TextBlock { Text = L.T("remote_this"), FontSize = 9.5f, Bold = true, Color = Theme.Muted, Align = align }, Pad, 452, ContentWidth, 24);
        Add(new TextBlock { Text = Environment.MachineName, FontSize = 11.5f, Bold = true, Align = align }, Pad, 476, ContentWidth - toggleWidth - 12, 26);
        Add(new TextBlock { Text = L.T(on ? "remote_on_hint" : "remote_off_hint"), FontSize = 9.5f, Color = on ? Theme.Good : Theme.Muted, Align = align }, Pad, 502, ContentWidth - toggleWidth - 12, 40);
        var toggle = Add(new PillButton { Text = L.T(on ? "remote_on" : "remote_off"), Glyph = Glyphs.Lock, Kind = on ? ButtonKind.Primary : ButtonKind.Ghost }, DesignWidth - Pad - toggleWidth, 482, toggleWidth, 42);
        toggle.Click += (_, _) =>
        {
            RemoteStore.Enabled = !on;
            if (on) StopRemote();
            else StartRemote();
            Navigate();
        };

        // Who gets in here without asking, once there's anyone.
        var trusted = RemoteStore.Trusted;
        if (trusted.Count > 0)
        {
            Add(new TextBlock { Text = L.T("remote_trusted"), FontSize = 9, Bold = true, Color = Theme.Muted, Align = align }, Pad, 550, ContentWidth, 22);
            y = 574;
            foreach (var device in trusted.Take(MaxRemoteTrustedRows))
            {
                Add(new TextBlock { Text = device.Name, FontSize = 10.5f, Align = align }, Pad, y, ContentWidth - 130, 34);
                var remove = Add(new PillButton { Text = L.T("remove"), Glyph = Glyphs.Delete, Kind = ButtonKind.Danger }, DesignWidth - Pad - 120, y, 120, 34);
                remove.Click += (_, _) =>
                {
                    RemoteStore.Untrust(device.Id);
                    _remote?.EndSession(); // if that's who's viewing now, taking it back should mean now
                    Navigate();
                };
                y += 40;
            }
        }

        AddCentered(new TextBlock { Text = L.T("remote_limits"), FontSize = 9, Color = Theme.Muted }, 700, ContentWidth, 36);

        if (!_remoteSearched) SearchRemote();
        if (!_remoteFirewallChecked && on) OpenRemoteFirewall();
    }

    async Task ViewAddress(string text)
    {
        if (text.Length == 0) return;
        System.Net.IPAddress? ip = null;
        if (!System.Net.IPAddress.TryParse(text, out ip))
        {
            try { ip = (await Dns.GetHostAddressesAsync(text)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
            catch (SocketException) { }
        }
        if (ip == null)
        {
            MessageBox.Show(this, L.T("remote_bad_address"), "Aura Mouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (LanScan.IsThisComputer(ip))
        {
            MessageBox.Show(this, L.T("remote_self"), "Aura Mouse", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var known = RemoteStore.Computers.FirstOrDefault(c => c.Ip == ip.ToString());
        OpenViewer(ip, known?.Name ?? text, known);
    }

    void OpenViewer(System.Net.IPAddress ip, string name, RemoteComputer? saved)
    {
        var viewer = new RemoteViewerForm(ip, name, saved);
        viewer.FormClosed += (_, _) => { if (_section == Section.Remote) Navigate(); };
        viewer.Show();
    }

    async void SearchRemote()
    {
        if (_remoteSearching) return;
        _remoteSearching = true;
        _remoteSearched = true;
        if (_section == Section.Remote) Navigate();
        try { _remoteFound = await Task.Run(() => RemoteDiscovery.FindAsync(CancellationToken.None)); }
        catch (OperationCanceledException) { }
        _remoteSearching = false;
        if (_section == Section.Remote) Navigate();
    }

    /// Others can only reach this computer through the firewall. Same rule as the mouse sharing's: it's per program.
    async void OpenRemoteFirewall()
    {
        _remoteFirewallChecked = true;
        if (await Task.Run(Firewall.IsOpen)) return;
        await Task.Run(Firewall.Open);
    }

    async void WakeUp(WakeTarget target)
    {
        int networks = await Wake.SendAsync(target.Macs);
        _wakeNoNetwork = networks == 0;
        if (networks > 0)
        {
            _wakeSent[target.Name] = DateTime.UtcNow;
            _wakeUp.Remove(target.Name);
        }
        UpdateWakeRows();
    }

    /// Only while that page is showing.
    async void CheckWakeTargets()
    {
        if (_wakeChecking || !Visible || _section != Section.Computers || _wakeRows.Count == 0) return;
        _wakeChecking = true;
        try
        {
            var targets = _wakeRows.Select(r => r.Target).ToList();
            var up = await Task.WhenAll(targets.Select(t => Wake.IsUpAsync(t.Ip)));
            for (int i = 0; i < targets.Count; i++)
            {
                if (up[i]) _wakeUp.Add(targets[i].Name);
                else _wakeUp.Remove(targets[i].Name);
            }
            UpdateWakeRows();
        }
        finally { _wakeChecking = false; }
    }

    void UpdateWakeRows()
    {
        foreach (var (target, status, button) in _wakeRows)
        {
            // Connected to us right now is the surest sign of all.
            bool up = _wakeUp.Contains(target.Name) || _peers.Any(p => p.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)) ||
                _clientConnected && string.Equals(_savedHost?.Name, target.Name, StringComparison.OrdinalIgnoreCase);
            if (up) _wakeSent.Remove(target.Name);

            string key = up ? "wake_online"
                : _wakeNoNetwork ? "wake_no_network"
                : !_wakeSent.TryGetValue(target.Name, out var sent) ? "wake_off"
                : DateTime.UtcNow - sent < TimeSpan.FromMinutes(2) ? "wake_waiting"
                : "wake_no_reply";
            status.Text = L.T(key);
            status.Color = up ? Theme.Good : Theme.Muted;
            status.Invalidate();
            button.Enabled = !up;
        }
    }

    void RenderSettings()
    {
        AddCentered(new TextBlock { Text = L.T("nav_settings"), FontSize = 18, Bold = true }, 84, ContentWidth, 40);

        bool on = AutoStart.Enabled;
        var startup = new PillButton { Text = L.T(on ? "on" : "off"), Glyph = Glyphs.Power, Kind = on ? ButtonKind.Primary : ButtonKind.Ghost };
        startup.Click += (_, _) =>
        {
            PairStore.SetAutoStart(!on);
            Navigate();
        };
        AddSettingRow(146, L.T("auto_start"), L.T("auto_start_tip"), startup);

        var hint = AddCentered(new TextBlock { Text = L.T("shortcut_click"), FontSize = 9.5f, Color = Theme.Muted }, 456, ContentWidth, 48);

        ShortcutBox Box(int[] keys, Func<int[]> other, Action<int[]> save)
        {
            var box = new ShortcutBox { Keys = keys, Other = other };
            box.Saved += k => { save(k); hint.Text = L.T("shortcut_click"); };
            box.TooFew += () => hint.Text = L.T("shortcut_two_keys");
            box.Clashes += () => hint.Text = L.T("shortcut_clash");
            return box;
        }
        AddSettingRow(250, L.T("lock_title"), L.T("lock_sub"), Box(Shortcuts.Lock, () => Shortcuts.Escape, Shortcuts.SetLock));
        AddSettingRow(354, L.T("emergency_title"), L.T("emergency_sub"), Box(Shortcuts.Escape, () => Shortcuts.Lock, Shortcuts.SetEscape));
    }

    void AddSettingRow(float y, string title, string subtitle, Control control)
    {
        const float controlWidth = 170;
        float textWidth = ContentWidth - controlWidth - 16;
        var align = L.Rtl ? StringAlignment.Far : StringAlignment.Near;
        Add(new TextBlock { Text = title, FontSize = 11, Bold = true, Align = align }, Pad, y, textWidth, 28);
        Add(new TextBlock { Text = subtitle, FontSize = 9.5f, Color = Theme.Muted, Align = align }, Pad, y + 28, textWidth, 56);
        Add(control, DesignWidth - Pad - controlWidth, y + 10, controlWidth, 48);
    }

    void RenderChoose()
    {
        AddCentered(new TextBlock { Text = L.T("choose_title"), FontSize = 20, Bold = true }, 84, ContentWidth, 42);
        AddCentered(new TextBlock { Text = L.T("tagline"), FontSize = 10.5f, Color = Theme.Muted }, 126, ContentWidth, 44);
        AddCentered(new ScreensArt { Connected = true, ThisLabel = L.T("this_pc"), OtherLabel = L.T("other_pc") },
            174, ContentWidth, 170);

        var host = Add(new ChoiceCard { Glyph = Glyphs.Mouse, Title = L.T("host_title"), Subtitle = L.T("host_sub"), Text = L.T("host_title") },
            Pad, 352, ContentWidth, 92);
        host.Click += (_, _) => StartHost();

        var client = Add(new ChoiceCard { Glyph = Glyphs.Monitor, Title = L.T("client_title"), Subtitle = L.T("client_sub"), Text = L.T("client_title") },
            Pad, 450, ContentWidth, 92);
        client.Click += (_, _) => StartClient();

        AddCentered(new TextBlock { Text = L.T("footer"), FontSize = 9.5f, Color = Theme.Muted }, 550, ContentWidth, 40);

        if (PairStore.Host is { } saved)
        {
            var reconnect = AddCentered(new PillButton { Text = string.Format(L.T("reconnect_to"), saved.Name), Glyph = Glyphs.Link, Kind = ButtonKind.Primary }, 606, ContentWidth, 46);
            reconnect.Click += (_, _) => StartClient(saved);
        }
    }

    /// Waiting: the code is the one thing that matters. Connected: the computers are, and the code steps back.
    void RenderHost()
    {
        bool codeLocked = _host?.CodeLocked == true;
        PillButton NewCodeButton(float y)
        {
            var renew = AddCentered(new PillButton { Text = L.T("new_code"), Glyph = Glyphs.Lock, Kind = ButtonKind.Primary }, y, 220, 38);
            renew.Click += (_, _) =>
            {
                _host?.RenewCode();
                _pairingCode = _host?.PairingCode ?? 0;
                SetStatus("status_waiting", Tone.Busy);
                Navigate();
            };
            return renew;
        }

        if (_peers.Count == 0)
        {
            AddCentered(new TextBlock { Text = L.T("pairing_code"), FontSize = 10, Bold = true, Color = Theme.Muted }, 92, ContentWidth, 24);
            _codeDisplay = AddCentered(new CodeDisplay { Code = codeLocked ? new string('-', CodeTiles.Digits) : _pairingCode.ToString() }, 120, 380, 78);
            if (codeLocked) NewCodeButton(212);
            else AddCentered(new TextBlock { Text = L.T("pairing_hint"), FontSize = 11 }, 206, ContentWidth, 48);
            AddCentered(new ScreensArt { Connected = false, ThisLabel = L.T("this_pc"), OtherLabel = L.T("other_pc") }, 268, ContentWidth, 178);
            _status = AddCentered(new StatusPill(), 462, ContentWidth, 56);
            AddCentered(new TextBlock { Text = L.T("encrypted"), FontSize = 9.5f, Color = Theme.Good, Glyph = Glyphs.Lock }, 530, ContentWidth, 22);
            var stopWaiting = AddCentered(new PillButton { Text = L.T("stop"), Glyph = Glyphs.Stop, Kind = ButtonKind.Danger }, 572, 200, 46);
            stopWaiting.Click += (_, _) => StopAll();
        }
        else
        {
            if (codeLocked) NewCodeButton(76);
            else _addAnother = AddCentered(new TextBlock { Text = string.Format(L.T("add_another"), _pairingCode), FontSize = 10.5f, Color = Theme.Muted, Glyph = Glyphs.Link }, 80, ContentWidth, 28);

            _layoutMap = AddCentered(new LayoutMap { ThisLabel = L.T("this_pc"), WhereLabel = L.T("where_is_it") }, 116, ContentWidth, 274);
            _layoutMap.SetPeers(_peers);
            _layoutMap.PeerDropped += (id, edge) => _host?.MovePeer(id, edge);
            AddCentered(new TextBlock { Text = L.T("arrange_hint"), FontSize = 9.5f, Color = Theme.Muted }, 392, ContentWidth, 40);

            _status = AddCentered(new StatusPill(), 438, ContentWidth, 56);
            AddCentered(new TextBlock { Text = L.T("encrypted"), FontSize = 9.5f, Color = Theme.Good, Glyph = Glyphs.Lock }, 504, ContentWidth, 22);
            AddCentered(new TextBlock { Text = string.Format(L.T("escape_hint"), Shortcuts.Describe(Shortcuts.Escape)), FontSize = 9.5f, Color = Theme.Muted, Glyph = Glyphs.Keyboard }, 526, ContentWidth, 22);
            AddCentered(new TextBlock { Text = L.T("drop_hint"), FontSize = 9.5f, Color = Theme.Muted, Glyph = Glyphs.Download }, 548, ContentWidth, 22);

            _sendButton = Add(new PillButton { Text = L.T("send_files"), Glyph = Glyphs.Upload, Kind = ButtonKind.Primary }, Pad, 590, 300, 46);
            _sendButton.Click += (_, _) => ChoosePeer(peerId => SendFromHost(peerId, PickFiles()));
            var stop = Add(new PillButton { Text = L.T("stop"), Glyph = Glyphs.Stop, Kind = ButtonKind.Danger }, Pad + 312, 590, ContentWidth - 312, 46);
            stop.Click += (_, _) => StopAll();
        }

        _status.Set(_statusText(), _statusTone);
        if (_peers.Count == 0 && _firewallKey != null) _status.Set(L.T(_firewallKey), Tone.Busy);
    }

    void RenderClient()
    {
        // Just the code: the computer showing it is found on the network behind the scenes.
        var titleText = _savedHost == null ? L.T("enter_code_title") : string.Format(L.T("reconnect_to"), _savedHost.Name);
        AddCentered(new TextBlock { Text = titleText, FontSize = 18, Bold = true }, 84, ContentWidth, 40);
        if (_savedHost == null)
            AddCentered(new TextBlock { Text = L.T("enter_code_sub"), FontSize = 10.5f, Color = Theme.Muted }, 126, ContentWidth, 26);
        AddCentered(new ScreensArt { Connected = _clientConnected, ThisLabel = L.T("this_pc"), OtherLabel = L.T("other_pc") },
            166, ContentWidth, 178);

        if (_savedHost == null)
        {
            _codeInput = AddCentered(new CodeInput { Code = _typedCode }, 364, 380, 78);
            _codeInput.CodeChanged += (_, _) =>
            {
                _typedCode = _codeInput.Code;
                TryAutoConnect();
            };
        }
        else AddCentered(new TextBlock { Text = L.T("saved_title"), FontSize = 11, Color = Theme.Good, Glyph = Glyphs.Lock }, 390, ContentWidth, 28);

        _status = AddCentered(new StatusPill(), 466, ContentWidth, 56);
        _status.Set(_statusText(), _statusTone);

        // Once connected, the main button sends files instead.
        // A remembered computer connects by itself, and doesn't rule out a new one.
        if (_savedHost != null && !_clientConnected)
        {
            var useCode = Add(new PillButton { Text = L.T("use_code"), Glyph = Glyphs.Link, Kind = ButtonKind.Primary }, Pad, 546, 300, 46);
            useCode.Click += (_, _) => StartClient();
        }
        else
        {
            _connectButton = _clientConnected
                ? Add(new PillButton { Text = L.T("send_files"), Glyph = Glyphs.Upload, Kind = ButtonKind.Primary }, Pad, 546, 300, 46)
                : Add(new PillButton { Text = L.T("connect"), Glyph = Glyphs.Link, Kind = ButtonKind.Primary }, Pad, 546, 300, 46);
            _connectButton.Click += (_, _) =>
            {
                if (_clientConnected) SendFromClient(PickFiles());
                else Connect();
            };
            UpdateConnectButton();
        }
        if (_clientConnected)
            AddCentered(new TextBlock { Text = L.T("drop_hint"), FontSize = 9.5f, Color = Theme.Muted, Glyph = Glyphs.Download }, 606, ContentWidth, 22);

        var back = Add(new PillButton { Text = L.T("back"), Glyph = L.Rtl ? Glyphs.Forward : Glyphs.Back },
            Pad + 312, 546, ContentWidth - 312, 46);
        back.Click += (_, _) => StopAll();

        BeginInvoke(() => _codeInput?.Focus());
    }

    // ───────────────────────── host ─────────────────────────

    void StartHost()
    {
        _section = Section.Connection;
        PairStore.BecomeHost();
        var host = new HostSession();
        host.LockChanged += OnLockChanged;
        host.StatusChanged += s => RunOnUi(() => { if (_host == host) OnHostStatus(s); });
        host.PeersChanged += () => RunOnUi(() => { if (_host == host) RefreshPeers(); });
        host.PairingCodeChanged += code => RunOnUi(() =>
        {
            if (_host != host) return;
            _pairingCode = code;
            if (_codeDisplay != null) _codeDisplay.Code = code.ToString();
            if (_addAnother != null) _addAnother.Text = string.Format(L.T("add_another"), code);
        });

        try
        {
            host.Start();
        }
        catch (SocketException)
        {
            host.Dispose();
            BringBack();
            MessageBox.Show(this, L.T("port_busy"), "Aura Mouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _host = host;
        _pairingCode = host.PairingCode;
        _peers = [];
        SetStatus("status_waiting", Tone.Busy);
        _page = Page.Host;
        Navigate();
        OpenFirewall(host);
    }

    /// Without this the other computers often can't reach this one at all, while connecting
    /// the other way around works, because only incoming connections get blocked.
    async void OpenFirewall(HostSession host)
    {
        if (await Task.Run(Firewall.IsOpen)) return;
        SetFirewallStatus(host, "firewall_asking");
        bool open = await Task.Run(Firewall.Open);
        SetFirewallStatus(host, open ? null : "firewall_declined");
    }

    void SetFirewallStatus(HostSession host, string? key)
    {
        if (_host != host) return;
        _firewallKey = key;
        if (_peers.Count > 0) return;
        if (key != null) SetStatus(key, Tone.Busy);
        else SetStatus("status_waiting", Tone.Busy);
    }

    void OnHostStatus(SessionStatus status)
    {
        switch (status)
        {
            case SessionStatus.WrongCodeAttempt:
                SetStatus("status_wrong_attempt", Tone.Bad); break;
            case SessionStatus.CodeLocked:
                SetStatus("status_code_locked", Tone.Bad);
                if (_section == Section.Connection) Navigate(); // the code goes, and the button for a new one appears
                break;
            // Connects and disconnects also raise PeersChanged, which RefreshPeers handles.
        }
    }

    void RefreshPeers()
    {
        if (_host == null) return;
        var previousCount = _peers.Count;
        _peers = _host.Peers;
        _layoutMap?.SetPeers(_peers);
        if (_sendButton != null) _sendButton.Enabled = _peers.Count > 0;
        if (_peers.Count > 0 && PairStore.EnableAutoStartOnce()) Navigate();
        else if (previousCount == 0 != (_peers.Count == 0) && _section == Section.Connection) Navigate(); // the code gives way to the map

        var unplaced = _peers.FirstOrDefault(p => p.Edge == Edge.None && !p.IsPhone);
        var active = _peers.FirstOrDefault(p => p.Active);
        var driving = _peers.FirstOrDefault(p => p.Driving);
        if (_peers.Count == 0)
        {
            // Keep "disconnected" visible if that's what just happened.
            if (previousCount == 0) SetStatus("status_waiting", Tone.Busy);
            else SetStatus("status_disconnected_host", Tone.Busy);
        }
        else if (unplaced.Name != null)
        {
            bool sidesTaken = _peers.Count(p => p.Edge.IsVertical()) >= 2;
            var name = unplaced.Name;
            SetStatus(() => string.Format(L.T(sidesTaken ? "status_place_drag" : "status_place"), name), Tone.Busy);
        }
        else if (driving.Name != null)
        {
            var name = driving.Name;
            SetStatus(() => string.Format(L.T("status_driving"), name), Tone.Good);
        }
        else if (active.Name != null)
        {
            var name = active.Name;
            SetStatus(() => string.Format(L.T("status_on_peer"), name), Tone.Good);
        }
        else
        {
            int count = _peers.Count;
            SetStatus(() => count == 1 ? L.T("status_connected_one") : string.Format(L.T("status_connected_many"), count), Tone.Good);
        }
    }

    // ───────────────────────── client ─────────────────────────

    void StartClient(SavedComputer? saved = null)
    {
        if (saved == null) _section = Section.Connection; // reconnecting by itself stays out of the way
        _client?.Dispose();
        var client = new ClientSession();
        client.LockChanged += OnLockChanged;
        client.StatusChanged += s => RunOnUi(() => { if (_client == client) OnClientStatus(s); });
        client.HostFound += (name, ip) => RunOnUi(() => { if (_client == client) AddDevice(name, ip); });
        client.Paired += pair =>
        {
            PairStore.SetHost(pair);
            PairStore.EnableAutoStartOnce();
            RunOnUi(() =>
            {
                if (_client != client) return;
                _savedHost = pair;
                Navigate(); // the auto-connect button appears
            });
        };

        _client = client;
        _savedHost = saved;
        _devices.Clear();
        _typedCode = "";
        _connecting = _clientConnected = false;
        if (saved == null) SetStatus("client_hint", Tone.Neutral);
        else SetStatus(() => string.Format(L.T("looking_for"), saved.Name), Tone.Busy);
        _page = Page.Client;
        Navigate();
        client.StartDiscovery();
        if (saved != null) KeepTrying(client, saved);
    }

    /// Goes on trying the saved computer until it's there, so they can be turned on in any order.
    async void KeepTrying(ClientSession client, SavedComputer saved)
    {
        while (client == _client && !_clientConnected)
        {
            var addresses = _devices.Select(d => d.Ip).ToList();
            if (IPAddress.TryParse(saved.Ip, out var last) && !addresses.Contains(last)) addresses.Insert(0, last);

            bool refused = false;
            foreach (var ip in addresses)
            {
                try
                {
                    if (await client.ConnectAsync(ip, saved))
                    {
                        if (client != _client) return;
                        PairStore.SetHost(saved with { Ip = ip.ToString() });
                        PairStore.EnableAutoStartOnce();
                        _clientConnected = true;
                        SetStatus("client_connected", Tone.Good);
                        Navigate();
                        return;
                    }
                    refused = true;
                }
                catch (Exception) { } // not there (yet)
                if (client != _client) return;
            }

            if (refused) SetStatus(() => string.Format(L.T("saved_refused"), saved.Name), Tone.Bad);
            await Task.Delay(2000);
        }
    }

    void OnClientStatus(SessionStatus status)
    {
        if (status == SessionStatus.Disconnected)
        {
            // A remembered computer: start looking for it again straight away.
            if (_savedHost is { } saved)
            {
                StartClient(saved);
                return;
            }
            _clientConnected = false;
            SetStatus("client_disconnected", Tone.Bad);
            Navigate(); // the main button goes back to "Connect"
        }
        // DiscoveryFailed (announcements can't be received) is fine: the direct network scan still finds the host.
    }

    void AddDevice(string name, IPAddress ip)
    {
        int existing = _devices.FindIndex(d => d.Ip.Equals(ip));
        if (existing >= 0)
        {
            // A scan may only know the address; the host's own announcement brings its real name.
            if (_devices[existing].Name == ip.ToString() && name != ip.ToString()) _devices[existing] = new Device(name, ip);
            return;
        }
        if (_devices.Count >= MaxDevices) return; // anyone on the network can claim to be a computer
        _devices.Add(new Device(Net.CleanName(name, ip.ToString()), ip));
        TryAutoConnect();
    }

    /// Connect as soon as the full code is typed; no extra click needed. If no computer has been
    /// found yet, Connect says it's still looking and AddDevice calls back here when one appears.
    void TryAutoConnect()
    {
        if (_typedCode.Length == CodeTiles.Digits) Connect();
    }

    /// Tries the code on each computer found; the one showing that code is the one that accepts it.
    async void Connect()
    {
        if (_client == null || _connecting || _clientConnected) return;
        if (_typedCode.Length != CodeTiles.Digits) { SetStatus("enter_code", Tone.Bad); _codeInput?.Focus(); return; }
        if (_devices.Count == 0) { SetStatus("waiting_for_host", Tone.Busy); return; }

        var client = _client;
        int code = int.Parse(_typedCode);
        bool wrongCode = false;
        _connecting = true;
        UpdateConnectButton();
        SetStatus("connecting", Tone.Busy);
        try
        {
            foreach (var device in _devices.ToList())
            {
                try
                {
                    if (await client.ConnectAsync(device.Ip, code, device.Name))
                    {
                        if (client != _client) return;
                        _clientConnected = true;
                        SetStatus("client_connected", Tone.Good);
                        Navigate(); // the main button becomes "Send files"
                        return;
                    }
                    wrongCode = true;
                }
                catch (Exception) { } // not reachable; try the next one
                if (client != _client) return;
            }

            SetStatus(wrongCode ? "wrong_code" : "cant_connect", Tone.Bad);
            if (wrongCode)
            {
                _typedCode = "";
                if (_codeInput != null) { _codeInput.Code = ""; _codeInput.Focus(); }
            }
        }
        finally
        {
            _connecting = false;
            UpdateConnectButton();
        }
    }

    void UpdateConnectButton()
    {
        if (_connectButton != null) _connectButton.Enabled = _clientConnected || !_connecting && _savedHost == null;
    }

    // ───────────────────────── files ─────────────────────────

    string[] PickFiles()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = L.T("pick_files") };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileNames : [];
    }

    /// With one computer connected, send straight to it; with several, ask which one.
    void ChoosePeer(Action<int> then)
    {
        if (_peers.Count == 0) { SetStatus("no_peers", Tone.Bad); return; }
        if (_peers.Count == 1) { then(_peers[0].Id); return; }

        var menu = new ContextMenuStrip { RightToLeft = L.Rtl ? RightToLeft.Yes : RightToLeft.No, ShowImageMargin = false };
        foreach (var peer in _peers)
        {
            var id = peer.Id;
            menu.Items.Add(peer.Name, null, (_, _) => then(id));
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(Cursor.Position);
    }

    async void SendFromHost(int peerId, IReadOnlyList<string> paths)
    {
        if (_host == null || paths.Count == 0) return;
        try { await _host.SendFilesAsync(peerId, paths); }
        catch (TransferRefusedException e) { SetStatus(e.ReasonKey, Tone.Bad); }
        catch (Exception) { SetStatus("err_generic", Tone.Bad); }
    }

    async void SendFromClient(IReadOnlyList<string> paths)
    {
        if (_client == null || paths.Count == 0) return;
        try { await _client.SendFilesAsync(paths); }
        catch (TransferRefusedException e) { SetStatus(e.ReasonKey, Tone.Bad); }
        catch (Exception) { SetStatus("err_generic", Tone.Bad); }
    }

    bool CanReceiveDrop => _page == Page.Host && _peers.Count > 0 || _page == Page.Client && _clientConnected;

    void OnDragOver(object? sender, DragEventArgs e) =>
        e.Effect = CanReceiveDrop && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (!CanReceiveDrop || e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (_page == Page.Client) { SendFromClient(paths); return; }

        // Dropped right on a computer in the map: send to that one.
        int target = _layoutMap?.PeerAtScreen(new Point(e.X, e.Y)) ?? -1;
        if (target >= 0) SendFromHost(target, paths);
        else ChoosePeer(peerId => SendFromHost(peerId, paths));
    }

    // ───────────────────────── shared ─────────────────────────

    /// A notice and a beep (the notice can't show over a fullscreen game): high for locked, low for free.
    void OnLockChanged(bool locked)
    {
        _ = Task.Run(() => Console.Beep(locked ? 880 : 440, 120));
        RunOnUi(() =>
        {
            DragUi.ShowNotice(L.T(locked ? "locked_notice" : "unlocked_notice"));
            Render(); // the lock button lights up while locked
        });
    }

    void StopAll()
    {
        _host?.Dispose();
        _host = null;
        _client?.Dispose();
        _client = null;
        _savedHost = null;
        _firewallKey = null;
        _peers = [];
        _clientConnected = false;
        _section = Section.Connection;
        _page = Page.Choose;
        Navigate();
    }

    void SetStatus(string key, Tone tone) => SetStatus(() => L.T(key), tone);

    void SetStatus(Func<string> text, Tone tone)
    {
        _statusText = text;
        _statusTone = tone;
        _status?.Set(text(), tone);
    }

    /// Rebuilds the page after the current click handler returns (it may dispose the clicked control).
    void Navigate() => RunOnUi(Render);

    void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(action);
    }

    static int S(float v) => (int)MathF.Round(v * Theme.Scale);

    /// Places a control in design coordinates, mirrored horizontally for Arabic.
    T Add<T>(T control, float x, float y, float w, float h) where T : Control =>
        Place(control, L.Rtl ? DesignWidth - x - w : SidebarWidth + x, y - PageTop, w, h);

    /// The sidebar sits on the left, or on the right in Arabic, with its contents mirrored too.
    T AddToSidebar<T>(T control, float x, float y, float w, float h) where T : Control =>
        Place(control, L.Rtl ? WindowWidth - x - w : x, y, w, h);

    T Place<T>(T control, float x, float y, float w, float h) where T : Control
    {
        control.Bounds = new Rectangle(S(x), S(y), S(w), S(h));
        // Files can be dropped anywhere on the window.
        control.AllowDrop = true;
        control.DragOver += OnDragOver;
        control.DragDrop += OnDragDrop;
        Controls.Add(control);
        return control;
    }

    T AddCentered<T>(T control, float y, float w, float h) where T : Control =>
        Add(control, (DesignWidth - w) / 2, y, w, h);

    // ───────────────────────── window ─────────────────────────

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ResumeLast();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Theme.Scale = e.DeviceDpiNew / 96f;
        Render();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _backdrop?.Dispose();
        _backdrop = null;
        Invalidate();
    }

    /// Dark background with two soft colour glows. Cached, because every transparent
    /// child repaints this behind itself on each animation frame.
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        _backdrop ??= CreateBackdrop(ClientSize, S(SidebarWidth));
        e.Graphics.DrawImageUnscaled(_backdrop, 0, 0);
    }

    static Bitmap CreateBackdrop(Size size, int sidebarWidth)
    {
        var bmp = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Theme.Background);
        Glow(g, new RectangleF(-size.Width * 0.45f, -size.Height * 0.35f, size.Width * 1.1f, size.Height * 0.8f), Theme.Accent, 48);
        Glow(g, new RectangleF(size.Width * 0.35f, size.Height * 0.55f, size.Width * 1.0f, size.Height * 0.8f), Theme.Accent2, 42);

        int edge = L.Rtl ? size.Width - sidebarWidth : sidebarWidth;
        using (var strip = new SolidBrush(Theme.WithAlpha(Theme.Surface, 170)))
            g.FillRectangle(strip, L.Rtl ? edge : 0, 0, sidebarWidth, size.Height);
        using (var line = new Pen(Theme.Border)) g.DrawLine(line, edge, 0, edge, size.Height);
        return bmp;
    }

    static void Glow(Graphics g, RectangleF area, Color color, int alpha)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(area);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Theme.WithAlpha(color, alpha),
            SurroundColors = [Color.FromArgb(0, color)],
        };
        g.FillPath(brush, path);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The X keeps it running in the tray, still connected. Windows shutting down still closes it.
        if (e.CloseReason == CloseReason.UserClosing && !_exiting)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown) _tray.ShowBalloonTip(3000, "Aura Mouse", L.T("tray_hint"), ToolTipIcon.None);
            _trayHintShown = true;
            return;
        }
        _tray.Dispose();
        _host?.Dispose();
        _client?.Dispose();
        base.OnFormClosing(e);
    }
}
