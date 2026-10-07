using System.Reflection;
using System.Runtime.Loader;
using System.ComponentModel;
using System.Runtime.InteropServices;

internal static class LayoutCheck
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("setup assembly and screenshot directory required");
        ApplicationConfiguration.Initialize();
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
        // Select installer presentation without Initialize(), cache preparation or registry reads.
        assembly.GetType("DotMic.Setup.PackageSource", true)!.GetField("installable", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
        var type = assembly.GetType("DotMic.Setup.SetupForm", true)!;
        using var form = (Form)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [Array.Empty<string>()], null)!;
        T Field<T>(string name) where T : Control => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var destination = Field<TextBox>("destination");
        var shortcut = Field<CheckBox>("desktopShortcut");
        var apply = Field<Button>("apply");
        var information = Field<TextBox>("information");
        if (!destination.Text.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) || !shortcut.Checked)
            throw new InvalidOperationException("Unexpected defaults");
        // Remove the sole production Shown handler before rendering. Its native/registry
        // initialization must never run in this presentation-only test.
        var key = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(f => f.Name.Equals("s_shownEvent", StringComparison.OrdinalIgnoreCase)).GetValue(null)!;
        var events = (EventHandlerList)typeof(Component).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        events.RemoveHandler(key, events[key]);
        form.StartPosition = FormStartPosition.Manual; form.Location = new(-32000, -32000); form.ShowInTaskbar = false;
        _ = form.Handle;
        SetWindowLong(form.Handle, -20, GetWindowLong(form.Handle, -20) | 0x08000000);
        form.Show(); Application.DoEvents();
        Directory.CreateDirectory(args[1]);
        foreach (int width in new[] { 480, 424 }) {
            form.ClientSize = new(width, 500);
            information.Text = "選択したマイクに音量補正・ゲート・ノイズ除去・音量制限を適用します。\r\n保護音声の互換設定は変更しません（設定済み）。\r\n一部の著作権保護コンテンツや保護音声の再生に影響する可能性があります。Microsoft認証版ではありません。\r\n音声・通話が一時的に切れます。パソコンは自動再起動しません。\r\nアプリ保存先：" + destination.Text;
            apply.Text = "同意して導入";
            _ = form.Handle;
            form.PerformLayout();
            form.Refresh(); Application.DoEvents();
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new(0, 0, form.Width, form.Height));
            int darkPixels = 0;
            for (int y = 60; y < image.Height - 45; ++y) for (int x = 20; x < image.Width - 20; ++x) {
                var pixel = image.GetPixel(x, y);
                if (pixel.R < 180 && pixel.G < 180 && pixel.B < 180) ++darkPixels;
            }
            if (darkPixels < 250) throw new InvalidOperationException("Offscreen image is blank: " + width);
            image.Save(Path.Combine(args[1], $"setup-consent-{width}.png"), System.Drawing.Imaging.ImageFormat.Png);
            foreach (var control in new Control[] { destination, shortcut, apply }) {
                Point position = Point.Empty;
                for (Control? current = control; current != null && current != form; current = current.Parent) position.Offset(current.Location);
                var bounds = new Rectangle(position, control.Size);
                if (!control.Visible || !form.ClientRectangle.Contains(bounds) || control.Width < 20 || control.Height < 15)
                    throw new InvalidOperationException("Control clipped: " + control.Name + "; " + bounds + "; client=" + form.ClientRectangle);
            }
            if (shortcut.Top >= apply.Parent!.Bottom) throw new InvalidOperationException("Shortcut not on consent page");
        }
        Console.WriteLine("PASS offscreen Setup consent layout, selectable Program Files path and optional desktop shortcut. No Shown/native calls, consent, registry, services or file installation.");
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
}
