using System.Reflection;
using System.Text;

namespace AuraMouse;

/// UI text. English lives here and is the fallback for anything not translated. Each file in i18n/ has one
/// line per line of en.txt, in the same order, and adding a language means adding a file and a line below.
internal static class L
{
    internal sealed record Language(string Code, string Name, bool Rtl = false, string Font = "Segoe UI");

    public static readonly Language[] Languages =
    [
        new("en", "English"),
        new("ar", "العربية", Rtl: true),
        new("es", "Español"),
        new("fr", "Français"),
        new("de", "Deutsch"),
        new("pt", "Português"),
        new("ru", "Русский"),
        new("tr", "Türkçe"),
        new("fa", "فارسی", Rtl: true),
        new("hi", "हिन्दी", Font: "Nirmala UI"),
        new("id", "Bahasa Indonesia"),
        new("zh", "简体中文", Font: "Microsoft YaHei UI"),
        new("ja", "日本語", Font: "Yu Gothic UI"),
        new("ko", "한국어", Font: "Malgun Gothic"),
    ];

    static string SettingsFile => Path.Combine(App.DataFolder, "language.txt");

    static Dictionary<string, string> _translated = [];

    public static Language Current { get; private set; } = Languages[0];

    public static bool Rtl => Current.Rtl;

    static readonly Dictionary<string, string> Texts = new()
    {
        ["nav_connection"] = "Connect",
        ["nav_remote"] = "Remote desktop",
        ["nav_computers"] = "Computers",
        ["nav_settings"] = "Settings",

        ["choose_title"] = "Connect your computers",
        ["tagline"] = "Use one mouse and keyboard across all of them, and move files between them",
        ["host_title"] = "Get a code",
        ["host_sub"] = "Start here on your first computer",
        ["client_title"] = "Enter a code",
        ["client_sub"] = "Use this on each of your other computers",
        ["footer"] = "Install Aura Mouse on each computer and keep them on the same network",
        ["this_pc"] = "This PC",
        ["other_pc"] = "Other PC",

        ["pairing_code"] = "YOUR CODE",
        ["pairing_hint"] = "On your other computer, open Aura Mouse, choose Enter a code, and type this",
        ["add_another"] = "Add another computer with code {0}",
        ["new_code"] = "New code",
        ["waiting_for_host"] = "Looking for the first computer… I'll connect as soon as I find it",
        ["arrange_hint"] = "Move the mouse toward a computer and I'll learn where it is, or drag it into place",
        ["where_is_it"] = "where is it?",
        ["encrypted"] = "Encrypted connection",
        ["escape_hint"] = "Mouse stuck? Press {0}",
        ["stop"] = "Stop",
        ["connect"] = "Connect",
        ["back"] = "Back",

        ["enter_code_title"] = "Enter the code",
        ["enter_code_sub"] = "It's shown on your other computer, under Get a code",
        ["client_hint"] = "Type the code and I'll connect by myself",
        ["reconnect_to"] = "Connect to {0}",
        ["looking_for"] = "Looking for {0}… I'll connect as soon as it's on",
        ["saved_title"] = "Saved, no code needed",
        ["saved_refused"] = "{0} no longer knows this computer. Pair again with a code using the button below",
        ["use_code"] = "New computer? Use a code",
        ["firewall_asking"] = "Windows will ask: press Yes so the other computer can reach this one",
        ["firewall_declined"] = "The firewall may block the other computer. If it can't connect, press Stop, start again and choose Yes",

        ["computers_title"] = "Your computers",
        ["computers_sub"] = "Saved computers connect without a code",
        ["computers_empty"] = "No saved computers yet",
        ["computers_add"] = "To add one, go to Connect and get a code",
        ["computer_is_client"] = "Connects to this computer",
        ["computer_is_host"] = "This computer connects to it",
        ["computer_connected"] = "Connected now ✓",
        ["remove"] = "Remove",
        ["forget"] = "Forget all",
        ["forget_confirm"] = "Forget all saved computers? They'll need a new code to connect again.",
        ["wake_button"] = "Turn on",
        ["wake_online"] = "It's on ✓",
        ["wake_off"] = "Off, or not answering",
        ["wake_waiting"] = "Signal sent, waiting for it to come on…",
        ["wake_no_reply"] = "No answer. The setup below may be incomplete, or it's on and a firewall blocks the check",
        ["wake_no_network"] = "This computer isn't on a network",
        ["wake_needs"] = "To turn on a computer, it needs a cable and power, Wake on LAN enabled in its BIOS and network card settings, and Fast Startup off",

        ["theme_title"] = "THEME",
        ["theme_dark"] = "Dark",
        ["theme_light"] = "Light",
        ["uninstall_title"] = "Remove from this PC",
        ["uninstall_sub"] = "Stops starting with Windows, removes its firewall rule, and forgets saved computers and settings. Received files stay",
        ["uninstall_confirm"] = "Remove Aura Mouse's settings from this PC? Saved computers will need a new code to connect again.",
        ["uninstall_done"] = "Done. You can now delete the Aura Mouse program file.",
        ["auto_start"] = "Start with Windows",
        ["auto_start_tip"] = "Starts hidden and repeats the last connection. Needed on every computer",
        ["on"] = "On",
        ["off"] = "Off",
        ["lock_mouse"] = "Lock mouse",
        ["mouse_locked"] = "Mouse locked",
        ["lock_title"] = "Lock the mouse to this screen",
        ["lock_sub"] = "For games: the mouse won't cross to the other screen until you press it again",
        ["emergency_title"] = "Emergency return",
        ["emergency_sub"] = "Brings the mouse back to this computer if it gets stuck on the other one",
        ["shortcut_press"] = "Press the keys together…",
        ["shortcut_click"] = "Click a shortcut to change it. Use any two keys or more. Esc cancels",
        ["shortcut_clash"] = "That overlaps the other shortcut. Choose different keys",
        ["shortcut_two_keys"] = "It needs at least two keys, so it can't trigger by accident in a game",
        ["locked_notice"] = "Mouse locked to this screen",
        ["unlocked_notice"] = "Mouse can cross screens again",

        ["tray_open"] = "Open Aura Mouse",
        ["tray_exit"] = "Exit",
        ["tray_hint"] = "Still running here. Right-click the icon to exit",

        ["remote_title"] = "Remote desktop",
        ["remote_sub"] = "See and control another computer, only with its owner's approval",
        ["remote_on"] = "Accepting requests",
        ["remote_off"] = "Not accepting",
        ["remote_on_hint"] = "Others can ask to come in, and you decide",
        ["remote_off_hint"] = "Nobody can ask to come into this computer",
        ["remote_address"] = "Computer's address or name",
        ["remote_connect"] = "Connect",
        ["remote_nearby"] = "COMPUTERS",
        ["remote_refresh"] = "Refresh",
        ["remote_searching"] = "Searching…",
        ["remote_none"] = "None found. Aura Mouse must be open on it, on the same network",
        ["remote_auto"] = "Enters without asking",
        ["remote_online"] = "Available",
        ["remote_offline"] = "Not visible now",
        ["remote_view"] = "View",
        ["remote_this"] = "THIS COMPUTER",
        ["remote_trusted"] = "DEVICES THAT ENTER THIS COMPUTER WITHOUT ASKING",
        ["remote_limits"] = "Can't reach the lock screen or administrator (UAC) prompts",
        ["remote_bad_address"] = "Couldn't find that address",
        ["remote_self"] = "That's this computer. Remote desktop is for reaching a different one",
        ["remote_ask"] = "{0} wants to see and control this computer",
        ["remote_ask_phone"] = "Phone {0} wants to see and control this computer",
        ["remote_ask_sub"] = "From {0}. If you don't recognise it, decline",
        ["remote_code_label"] = "The same number must show on their screen",
        ["remote_trust_box"] = "Let this device in without asking from now on",
        ["remote_allow"] = "Allow",
        ["remote_decline"] = "Decline",
        ["remote_banner"] = "{0} is viewing and controlling this PC",
        ["remote_end"] = "End",
        ["remote_full_screen"] = "Full screen",
        ["remote_monitor"] = "Next screen",
        ["remote_connecting"] = "Connecting…",
        ["remote_waiting"] = "Waiting for the owner to approve\nTheir screen should show: {0}",
        ["remote_declined"] = "The owner didn't approve",
        ["remote_busy"] = "Another device is on that computer right now",
        ["remote_unreachable"] = "Couldn't reach it. Check Aura Mouse is open there and accepting requests",
        ["remote_lost"] = "The session ended",

        ["connecting"] = "Connecting…",
        ["client_connected"] = "Connected ✓ Move either computer's mouse across the edge facing the other",
        ["client_disconnected"] = "Disconnected",
        ["wrong_code"] = "Wrong code, try again",
        ["cant_connect"] = "Couldn't connect. Check both computers are on the same network and Aura Mouse is allowed through the firewall",
        ["enter_code"] = "Type the 6-digit code",

        ["status_waiting"] = "Waiting for the other computer…",
        ["status_connected_one"] = "Connected to 1 computer ✓ Move the mouse toward it",
        ["status_connected_many"] = "Connected to {0} computers ✓ Move the mouse toward the one you want",
        ["status_place"] = "{0} connected ✓ Move the mouse to the left or right edge on its side so I learn where it is",
        ["status_place_drag"] = "{0} connected ✓ Left and right are taken, so move the mouse to the top or bottom edge on its side, or drag it into place",
        ["status_driving"] = "{0}'s mouse and keyboard are in control now",
        ["status_on_peer"] = "The mouse is on {0} now",
        ["status_disconnected_host"] = "Disconnected. Waiting for the other computer…",
        ["status_wrong_attempt"] = "Someone tried to connect with a wrong code",
        ["status_code_locked"] = "Too many wrong tries, so the code is off for safety. Paired computers still work",

        ["send_files"] = "Send files",
        ["drop_hint"] = "You can also drag files and folders onto this window",
        ["no_peers"] = "No computer is connected yet",
        ["pick_files"] = "Pick the files to send",
        ["transfers_title"] = "File transfers",
        ["to_peer"] = "to {0}",
        ["from_peer"] = "from {0}",
        ["n_files"] = "{0} files",
        ["one_file"] = "1 file",
        ["state_waiting"] = "Waiting for the other computer…",
        ["state_retrying"] = "Interrupted, resuming from where it stopped…",
        ["state_done"] = "Done ✓",
        ["state_canceled"] = "Canceled",
        ["time_left"] = "{0} left",
        ["of"] = "{0} of {1}",
        ["open_folder"] = "Open folder",
        ["cancel"] = "Cancel",
        ["dismiss"] = "Dismiss",
        ["err_disconnected"] = "Disconnected. Send the same files again and it will resume where it stopped",
        ["err_space_remote"] = "Not enough free space on the other computer",
        ["err_space_local"] = "Not enough free space on this computer",
        ["err_canceled_remote"] = "The other computer canceled it",
        ["err_source_changed"] = "A file changed while sending. Send it again",
        ["err_too_many"] = "Too many files (over 100,000)",
        ["err_generic"] = "Something went wrong",
        ["port_busy"] = "Aura Mouse is already running, or another app is using its port.",
        ["admin_in_front"] = "{0} runs as administrator, so Aura Mouse can't control it. The mouse came back here.",
        ["admin_in_front_unnamed"] = "This program runs as administrator, so Aura Mouse can't control it. The mouse came back here.",
        ["admin_restart"] = "Restart as administrator",
        ["not_now"] = "Not now",
        ["admin_now"] = "Aura Mouse is running as administrator",
        ["shutdown_soon"] = "This computer will turn off in {0} seconds. Asked for from a paired phone.",
        ["shutdown_cancel"] = "Don't turn off",
        ["shutdown_canceled"] = "Shutdown cancelled",
        ["shutdown_warning"] = "Aura Mouse is turning this computer off, asked for from your phone.",
    };

    public static string T(string key)
    {
        if (!Texts.TryGetValue(key, out var english)) return key;
        return _translated.TryGetValue(english, out var translated) ? translated : english;
    }

    public static void Set(string code)
    {
        var language = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[0];
        var map = new Dictionary<string, string>();
        if (language.Code != "en")
        {
            var english = ReadLines("en");
            var lines = ReadLines(language.Code);
            for (int i = 0; i < english.Length && i < lines.Length; i++)
                if (lines[i].Length > 0) map[english[i]] = lines[i];
        }
        _translated = map;
        Current = language;
    }

    static string[] ReadLines(string code)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("lang." + code);
        if (stream == null) return [];
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n').Select(line => line.TrimEnd('\r').Replace("\\n", "\n")).ToArray();
    }

    /// English unless the person chose otherwise.
    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                Set(File.ReadAllText(SettingsFile).Trim());
                return;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Set("en");
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, Current.Code);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
