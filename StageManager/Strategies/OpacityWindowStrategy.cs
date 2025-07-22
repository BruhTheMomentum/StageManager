using StageManager.Native.Window;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic; // Added for Dictionary

namespace StageManager.Strategies
{
	/// <summary>
	/// Works well with opacity = 0, higher opacity will make the windows appear when clicked
	/// Visual Studio cannot be hidden this way, might be the same with other windows
	/// </summary>
	internal class OpacityWindowStrategy : IWindowStrategy
	{
		[DllImport("user32.dll")]
		static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

		[DllImport("user32.dll")]
		static extern int GetWindowLong(IntPtr hWnd, int nIndex);

		[DllImport("user32.dll")]
		public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

		public const int GWL_EXSTYLE = -20;
		public const int WS_EX_LAYERED = 0x80000;
		public const int WS_EX_TRANSPARENT = 0x20;   // ignore mouse / hit-testing
		public const int LWA_ALPHA = 0x2;

		// Remember previous styles so we can restore them when showing again.
		private static readonly Dictionary<IntPtr, int> _originalStyles = new();
		// Remember original on-screen position when we had to move the window off-screen
		private static readonly Dictionary<IntPtr, (int X, int Y)> _originalPositions = new();

		public void Show(IWindow window)
		{
			var hWnd = window.Handle;
			var exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);

			// Restore original extended style if we stored one
			if (_originalStyles.TryGetValue(hWnd, out var original))
			{
				SetWindowLong(hWnd, GWL_EXSTYLE, original);
				_originalStyles.Remove(hWnd);
			}

			// Restore original on-screen position if we moved it off-screen
			if (_originalPositions.TryGetValue(hWnd, out var pos))
			{
				StageManager.Native.PInvoke.Win32.SetWindowPos(hWnd, IntPtr.Zero,
					pos.X, pos.Y, 0, 0,
					StageManager.Native.PInvoke.Win32.SetWindowPosFlags.IgnoreResize |
					StageManager.Native.PInvoke.Win32.SetWindowPosFlags.DoNotActivate);
				_originalPositions.Remove(hWnd);
			}

			// Ensure WS_EX_LAYERED remains so we can control alpha smoothly
			if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_LAYERED) == 0)
			{
				SetWindowLong(hWnd, GWL_EXSTYLE, GetWindowLong(hWnd, GWL_EXSTYLE) | WS_EX_LAYERED);
			}

			// Set full opacity again
			SetLayeredWindowAttributes(hWnd, 0, 255, LWA_ALPHA);

			window.BringToTop();
		}

		public void Hide(IWindow window)
		{
			var hWnd = window.Handle;

			// Store original exstyle once
			if (!_originalStyles.ContainsKey(hWnd))
			{
				_originalStyles[hWnd] = GetWindowLong(hWnd, GWL_EXSTYLE);
			}

			// Enable layered + transparent styles
			var newStyle = GetWindowLong(hWnd, GWL_EXSTYLE) | WS_EX_LAYERED | WS_EX_TRANSPARENT;
			SetWindowLong(hWnd, GWL_EXSTYLE, newStyle);

			// Make fully transparent (alpha = 0)
			bool result = SetLayeredWindowAttributes(hWnd, 0, 0, LWA_ALPHA);

			if (!result)
			{
				// Fallback: move off-screen if transparency not supported
				try
				{
					// Store original position only once
					if (!_originalPositions.ContainsKey(hWnd))
					{
						StageManager.Native.PInvoke.Win32.Rect rect = new StageManager.Native.PInvoke.Win32.Rect();
						StageManager.Native.PInvoke.Win32.GetWindowRect(hWnd, ref rect);
						_originalPositions[hWnd] = (rect.Left, rect.Top);
					}

					const int OFFSCREEN_OFFSET = 4000; // beyond typical virtual screen bounds
					StageManager.Native.PInvoke.Win32.SetWindowPos(hWnd, IntPtr.Zero,
						OFFSCREEN_OFFSET, OFFSCREEN_OFFSET, 0, 0,
						StageManager.Native.PInvoke.Win32.SetWindowPosFlags.IgnoreResize |
						StageManager.Native.PInvoke.Win32.SetWindowPosFlags.DoNotActivate);
				}
				catch { /* ignored */ }
			}
		}
	}
}