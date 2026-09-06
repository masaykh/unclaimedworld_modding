using System;
#if UW_DX
using System.Windows.Forms;
#endif

namespace UWGame.Port;

/// <summary>
/// PORT DEVIATION 10 (see PORTING-NOTES.md).
///
/// The last two places the game genuinely needs a platform-specific window API, isolated here
/// so that nothing else in the 2,170-type code base has to know which MonoGame backend it is
/// running on. Everything else that used to need WinForms has been replaced with a
/// cross-platform MonoGame equivalent instead of being conditionally compiled:
///
///   * the borderless toggle was `Control.FromHandle(...).FindForm().FormBorderStyle`; it is
///     now <c>GameWindow.IsBorderless</c>, which MonoGame supports on both WindowsDX and
///     DesktopGL;
///   * the mouse cursors were WinForms <c>Cursor</c> objects built through user32; they are now
///     <c>MouseCursor</c> / <c>Mouse.SetCursor</c> (deviation 9);
///   * <c>Screen.AllScreens</c> monitor placement is now <c>GraphicsAdapter</c> +
///     <c>GameWindow.Position</c> (deviation 10, in Program.cs).
///
/// What is left really is Windows-only: a modal error dialog owned by the game window, and a
/// focus-state diagnostic that only means anything on the DirectX backend.
/// </summary>
internal static class PlatformWindow
{
#if UW_DX
	/// <summary>Lets a WinForms MessageBox be owned by MonoGame's game window.</summary>
	private sealed class WindowHandle : IWin32Window
	{
		public IntPtr Handle { get; }

		public WindowHandle(IntPtr handle)
		{
			Handle = handle;
		}
	}
#endif

	/// <summary>
	/// Shows the game's fatal-error report. The caller has already written it to Errors.txt
	/// (deviation 6), so if no dialog can be shown the report is not lost.
	/// </summary>
	/// <returns>true if a dialog was actually displayed.</returns>
	public static bool ShowErrorDialog(IntPtr windowHandle, string text, string title)
	{
#if UW_DX
		MessageBox.Show(new WindowHandle(windowHandle), text, title, MessageBoxButtons.OK, MessageBoxIcon.Hand);
		return true;
#else
		// DesktopGL has no message-box API surfaced by MonoGame, and SDL_ShowSimpleMessageBox
		// is not exposed. The report is already in Errors.txt; echo it so a terminal run shows
		// it too. Worth revisiting if the game ever grows an in-game error screen, which would
		// be better than a native dialog on every platform.
		Console.Error.WriteLine("=== " + title + " ===");
		Console.Error.WriteLine(text);
		return false;
#endif
	}

	/// <summary>
	/// Describes the window's focus state for the fullscreen-failure diagnostic. The DirectX
	/// backend can report the full WinForms focus chain, which is the whole point of the
	/// message: DXGI refuses an exclusive-fullscreen swap chain when the window is not
	/// foreground. That failure mode - and the string the retry loop matches,
	/// "DXGI_ERROR_NOT_CURRENTLY_AVAILABLE" - does not exist on OpenGL.
	/// </summary>
	public static string DescribeFocus(IntPtr windowHandle, bool isActive)
	{
#if UW_DX
		Form form = System.Windows.Forms.Control.FromHandle(windowHandle)?.FindForm();
		if (form != null)
		{
			return string.Format("IsActive: {0}, TopMost: {1}, Focused: {2}, ContainsFocus: {3}",
				isActive, form.TopMost, form.Focused, form.ContainsFocus) + Environment.NewLine;
		}
#endif
		return string.Format("IsActive: {0}", isActive) + Environment.NewLine;
	}

	/// <summary>
	/// Moves the game window onto the monitor selected by Options.TargetMonitorNumber
	/// (1-based; 0 means "leave it alone").
	///
	/// Was: cast the window handle to a WinForms Form, set StartPosition = Manual and
	/// Location = Screen.AllScreens[n-1].WorkingArea.Location. MonoGame 3.8 exposes
	/// GameWindow.Position but has no portable way to enumerate monitor desktop origins, so
	/// this is one of the few places that genuinely cannot be made platform-neutral today.
	/// The DirectX path keeps the original behaviour exactly.
	/// </summary>
	public static void PlaceWindowOnMonitor(Microsoft.Xna.Framework.GameWindow window, int targetMonitorNumber)
	{
#if UW_DX
		Form form = System.Windows.Forms.Control.FromHandle(window.Handle)?.FindForm();
		if (form == null)
		{
			return;
		}
		Screen[] allScreens = Screen.AllScreens;
		Screen screen = Screen.PrimaryScreen;
		if (targetMonitorNumber > 0 && targetMonitorNumber <= allScreens.Length)
		{
			screen = allScreens[targetMonitorNumber - 1];
		}
		form.StartPosition = FormStartPosition.Manual;
		form.Location = screen.WorkingArea.Location;
#else
		// MonoGame's DesktopGL backend does not surface per-display desktop bounds (SDL has
		// them, MonoGame does not expose them), so anything other than "default placement"
		// cannot be honoured here yet. Say so rather than silently ignoring the setting.
		if (targetMonitorNumber > 1)
		{
			Console.Error.WriteLine(
				"Options.TargetMonitorNumber = " + targetMonitorNumber +
				" is not supported on this backend; using the default monitor.");
		}
#endif
	}

	/// <summary>
	/// The game requires Windows Media Player's codecs because its music is WMA, played through
	/// MonoGame's MediaFoundation-backed Song on the DirectX backend. Program.Main gated startup
	/// on this and showed a MessageBox with download links when it was missing.
	///
	/// This gate SURVIVES PORT DEVIATION 17. Replacing the WMV menu background with a
	/// motion-JPEG sequence removed MediaFoundation's VIDEO use, but the nine WMA music tracks
	/// still go through it, so the codec requirement is unchanged on DirectX. Retiring this check
	/// would need the music transcoded to Ogg Vorbis as well - which build/32-convert-media.sh
	/// already does for the OpenGL package, so the pieces exist; it is simply not done for the
	/// DirectX build, where the tracks are played as shipped.
	///
	/// Only meaningful on the DirectX backend. The OpenGL backend does not use MediaFoundation
	/// at all - it needs the media transcoded to Vorbis/Theora instead, which is tracked
	/// separately - so gating startup on a Windows registry key there would be simply wrong.
	/// </summary>
	/// <returns>null when startup may proceed, otherwise the message to show the player.</returns>
	public static string CheckMediaCodecsAvailable()
	{
#if UW_DX
		object value = Microsoft.Win32.Registry.GetValue(
			"HKEY_LOCAL_MACHINE\\Software\\Microsoft\\Active Setup\\Installed Components\\{22d6f312-b0f6-11d0-94ab-0080c74c7e95}",
			"IsInstalled", null);
		if (value == null || value.ToString() != "1")
		{
			return "It appears that you don't have Windows Media Player installed. This game needs system features bound to Windows Media Player. Please install the Media Feature Pack corresponding to your Windows version to run this game:"
				+ Environment.NewLine + Environment.NewLine
				+ "https://support.microsoft.com/help/3145500/media-feature-pack-list-for-windows-n-editions";
		}
#endif
		return null;
	}

	/// <summary>
	/// Forces the game window foreground between fullscreen retries. Only the DirectX backend
	/// needs it: it exists to satisfy DXGI's requirement that the window be foreground before
	/// an exclusive-fullscreen swap chain will be granted. Harmlessly a no-op elsewhere.
	/// </summary>
	public static void BringToFront(IntPtr windowHandle)
	{
#if UW_DX
		Form form = System.Windows.Forms.Control.FromHandle(windowHandle)?.FindForm();
		if (form != null)
		{
			form.TopMost = true;
			form.BringToFront();
		}
#endif
	}
}
