﻿using AsyncAwaitBestPractices;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Strategies;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace StageManager
{
	public class SceneManager
	{
		private readonly Desktop _desktop;
		private List<Scene> _scenes;
		private Scene _current;
		private bool _suspend = false;
		private Guid? _reentrancyLockSceneId;
		private Scene _lastScene; // remembers the scene that was active before desktop view
		private DateTime _lastDesktopToggle = DateTime.MinValue;
		private IWindow _lastFocusedWindow;

		public event EventHandler<SceneChangedEventArgs> SceneChanged;
		public event EventHandler<CurrentSceneSelectionChangedEventArgs> CurrentSceneSelectionChanged;

		private IWindowStrategy WindowStrategy { get; } = new NormalizeAndMinimizeWindowStrategy(); // new WindowNormalizeStrategy/OpacityWindowStrategy/ShowAndHideWindowStrategy

		public WindowsManager WindowsManager { get; }

		public SceneManager(WindowsManager windowsManager)
		{
			WindowsManager = windowsManager ?? throw new ArgumentNullException(nameof(windowsManager));
			_desktop = new Desktop();
			_desktop.HideIcons();
		}

		public async Task Start()
		{
			if (Thread.CurrentThread.ManagedThreadId != 1)
				throw new NotSupportedException("Start has to be called on the main thread, otherwise events won't be fired.");

			WindowsManager.WindowCreated += WindowsManager_WindowCreated;
			WindowsManager.WindowUpdated += WindowsManager_WindowUpdated;
			WindowsManager.WindowDestroyed += WindowsManager_WindowDestroyed;
			WindowsManager.UntrackedFocus += WindowsManager_UntrackedFocus;
			WindowsManager.DesktopShortClick += WindowsManager_DesktopShortClick;

			await WindowsManager.Start();
		}

		internal void Stop()
		{
			WindowsManager.Stop();

			foreach (var scene in _scenes)
			{
				foreach (var w in scene.Windows)
					WindowStrategy.Show(w);
			}

			_desktop.ShowIcons();
		}

		private void WindowsManager_WindowUpdated(IWindow window, WindowUpdateType type)
		{
			if (_suspend)
				return;

			if (type == WindowUpdateType.Foreground)
			{
				_lastFocusedWindow = window; // remember for scene restore
				SwitchToSceneByWindow(window).SafeFireAndForget();
			}
		}

		private bool IsBlankDesktopClick(IntPtr handle)
		{
			// Determine window class
			var sb = new StringBuilder(256);
			Win32.GetClassName(handle, sb, sb.Capacity);
			var cls = sb.ToString();

			// Ignore taskbar / other common shells
			if (string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(cls, "TrayNotifyWnd", StringComparison.OrdinalIgnoreCase))
				return false;

			// Helper local function to evaluate selection count on a SysListView32 window
			static bool IsListViewSelectionEmpty(IntPtr listView)
			{
				if (listView == IntPtr.Zero)
					return true;

				var sel = Win32.SendMessage(listView, Win32.LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero);
				return sel == IntPtr.Zero;
			}

			// Desktop background container windows (WorkerW/Progman)
			if (string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase))
			{
				// A click on these windows is only considered a blank desktop click when
				// no desktop icons are currently selected. Otherwise it is an icon click.

				// Find the SHELLDLL_DefView child hosting the desktop list view
				var shell = Desktop.FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null);
				// Within the DefView find the SysListView32 control that displays the icons
				var listView = shell != IntPtr.Zero ? Desktop.FindWindowEx(shell, IntPtr.Zero, "SysListView32", null) : IntPtr.Zero;

				return IsListViewSelectionEmpty(listView);
			}

			// Desktop icon view (list view) – ensure no icon is selected
			if (string.Equals(cls, "SysListView32", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(cls, "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase))
			{
				return IsListViewSelectionEmpty(handle);
			}

			return false;
		}

		private void WindowsManager_UntrackedFocus(object? sender, IntPtr e)
		{
			// Let dedicated mouse-click handler manage desktop toggling.
			if (IsBlankDesktopClick(e))
				return;

			// Potentially remember desktop view handle for future use
			if (!_desktop.HasDesktopView)
				_desktop.TrySetDesktopView(e);

			// No scene switching here – handled by DesktopShortClick or other logic.
		}

		private void WindowsManager_DesktopShortClick(object? sender, IntPtr handle)
		{
			if (_suspend)
				return;

			// Only treat clicks on truly blank desktop areas as a toggle trigger
			if (!IsBlankDesktopClick(handle))
				return;

			// Debounce additional toggles happening too quickly (double-click already filtered by WindowsManager)
			var now = DateTime.Now;
			if ((now - _lastDesktopToggle).TotalMilliseconds < 100)
				return;

			_lastDesktopToggle = now;

			if (_current is null)
			{
				if (_lastScene is object)
					SwitchTo(_lastScene).SafeFireAndForget();
			}
			else
			{
				SwitchTo(null).SafeFireAndForget();
			}
		}

		private void WindowsManager_WindowDestroyed(IWindow window)
		{
			var scene = FindSceneForWindow(window);

			if (scene is not null)
			{
				scene.Remove(window);

				if (scene.Windows.Any())
				{
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
				}
				else
				{
					_scenes.Remove(scene);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Removed));
				}
			}
		}

		public Scene FindSceneForWindow(IWindow window) => FindSceneForWindow(window.Handle);

		public Scene FindSceneForWindow(IntPtr handle) => _scenes?.FirstOrDefault(s => s.Windows.Any(w => w.Handle == handle));

		private Scene FindSceneForProcess(string processName) => _scenes.FirstOrDefault(s => string.Equals(s.Key, processName, StringComparison.OrdinalIgnoreCase));

		private async void WindowsManager_WindowCreated(IWindow window, bool firstCreate)
		{
			SwitchToSceneByNewWindow(window).SafeFireAndForget();
		}

		private async Task SwitchToSceneByWindow(IWindow window)
		{
			var scene = FindSceneForWindow(window);
			if (scene is null)
			{
				scene = new Scene(GetWindowGroupKey(window), window);
				_scenes.Add(scene);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));
			}

			await SwitchTo(scene);
		}

		private async Task SwitchToSceneByNewWindow(IWindow window)
		{
			var existentScene = FindSceneForProcess(GetWindowGroupKey(window));
			var scene = existentScene ?? new Scene(window.ProcessName, window);

			if (existentScene is null)
			{
				_scenes.Add(scene);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));
			}
			else
			{
				scene.Add(window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			}

			await SwitchTo(scene).ConfigureAwait(true);
		}

		/// <summary>
		/// Determines if a scene is switched back to shortly after it has been hidden.
		/// This can happen if an app activates one of it's windows after being hidde,
		/// like Microsoft Teams does if there's a small floating window for a current call.
		/// </summary>
		/// <param name="scene"></param>
		/// <returns></returns>
		private bool IsReentrancy(Scene? scene)
		{
			if (scene is null)
				return false;

			if (Guid.Equals(scene.Id, _reentrancyLockSceneId))
				return true;

			if (_current is object)
			{
				_reentrancyLockSceneId = _current.Id;

				Task.Run(async () =>
				{
					await Task.Delay(1000).ConfigureAwait(false);
					_reentrancyLockSceneId = null;
				}).SafeFireAndForget();
			}

			return false;
		}

		public async Task SwitchTo(Scene? scene)
		{
			if (object.Equals(scene, _current))
				return;

			if (IsReentrancy(scene))
				return;

			IWindow focusCandidate = null;

			try
			{
				_suspend = true;

				var otherWindows = GetSceneableWindows().Except(scene?.Windows ?? Array.Empty<IWindow>()).ToArray();

				var prior = _current;
				_current = scene;

				foreach (var s in _scenes)
					{s.IsSelected = s.Equals(scene);}

				if (scene is object)
				{
					foreach (var w in scene.Windows)
						WindowStrategy.Show(w);

					// Determine which window should get focus after restore
					if (_lastFocusedWindow is object && scene.Windows.Contains(_lastFocusedWindow))
						focusCandidate = _lastFocusedWindow;
					else
						focusCandidate = scene.Windows.FirstOrDefault();
				}

				foreach (var o in otherWindows)
					WindowStrategy.Hide(o);

				CurrentSceneSelectionChanged?.Invoke(this, new CurrentSceneSelectionChangedEventArgs(prior, _current));

				if (scene is null)
				{
					_lastScene = prior;
					_desktop.ShowIcons();
				}
				else
				{
					_lastScene = null;
					_desktop.HideIcons();
				}
			}
			finally
			{
				_suspend = false;

				// Apply focus once suspension lifted
				if (focusCandidate is object)
					focusCandidate.Focus();
			}
		}

		public Task MoveWindow(Scene sourceScene, IWindow window, Scene targetScene)
		{
			try
			{
				_suspend = true;

				if (sourceScene is null || sourceScene.Equals(targetScene))
					return Task.CompletedTask;

				sourceScene.Remove(window);
				targetScene.Add(window);

				SceneChanged?.Invoke(this, new SceneChangedEventArgs(sourceScene, window, ChangeType.Updated));
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(targetScene, window, ChangeType.Updated));

				if (!sourceScene.Windows.Any())
				{
					_scenes.Remove(sourceScene);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(sourceScene, window, ChangeType.Removed));
				}

				if (targetScene.Equals(_current))
				{
					WindowStrategy.Show(window);
					window.Focus();
				}
				else
				{
					WindowStrategy.Hide(window);

					// reset window position after move so that the window is back at the starting position on the new scene
					if (window is WindowsWindow w && w.PopLastLocation() is IWindowLocation l)
						Win32.SetWindowPos(window.Handle, IntPtr.Zero, l.X, l.Y, 0, 0, Win32.SetWindowPosFlags.IgnoreResize);
				}

				return Task.CompletedTask;
			}
			finally
			{
				_suspend = false;
			}
		}

		public async Task MoveWindow(IntPtr handle, Scene targetScene)
		{
			var source = FindSceneForWindow(handle);

			if (source is null || source.Equals(targetScene))
				return;

			var window = source.Windows.First(w => w.Handle == handle);
			await MoveWindow(source, window, targetScene);
		}

		public async Task PopWindowFrom(Scene sourceScene)
		{
			if (sourceScene is null || _current is null || sourceScene.Equals(_current))
				return;

			var window = sourceScene.Windows.LastOrDefault();

			if (window is object)
				await MoveWindow(sourceScene, window, _current).ConfigureAwait(false);
		}

		private IEnumerable<IWindow> GetSceneableWindows() => WindowsManager?.Windows?.Where(w => w.CanLayout && !string.IsNullOrEmpty(w.ProcessFileName) && !string.IsNullOrEmpty(w.Title));

		public IEnumerable<Scene> GetScenes()
		{
			if (_scenes is null)
			{
				_scenes = GetSceneableWindows()
							.GroupBy(GetWindowGroupKey)
							.Select(group => new Scene(group.Key, group.ToArray()))
							.ToList();
			}

			return _scenes;
		}

		public IEnumerable<IWindow> GetCurrentWindows() => _current?.Windows ?? GetSceneableWindows();

		private string GetWindowGroupKey(IWindow window) => window.ProcessName;
	}
}
