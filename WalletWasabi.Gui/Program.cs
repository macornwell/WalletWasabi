﻿using Avalonia;
using AvalonStudio.Shell;
using System;
using System.IO;
using System.Threading.Tasks;
using WalletWasabi.Gui.ViewModels;
using WalletWasabi.Logging;

namespace WalletWasabi.Gui
{
	internal class Program
	{
#pragma warning disable IDE1006 // Naming Styles

		private static async Task Main(string[] args)
#pragma warning restore IDE1006 // Naming Styles
		{
			StatusBarViewModel statusBar = null;
			try
			{
				Logger.SetFilePath(Path.Combine(Global.DataDir, "Logs.txt"));
#if RELEASE
				Logger.SetMinimumLevel(LogLevel.Info);
				Logger.SetModes(LogMode.File);
#else
				Logger.SetMinimumLevel(LogLevel.Debug);
				Logger.SetModes(LogMode.Debug, LogMode.Console, LogMode.File);
#endif
				var configFilePath = Path.Combine(Global.DataDir, "Config.json");
				var config = new Config(configFilePath);
				await config.LoadOrCreateDefaultFileAsync();
				Logger.LogInfo<Config>("Config is successfully initialized.");

				Global.Initialize(config);
				statusBar = new StatusBarViewModel(Global.Nodes.ConnectedNodes, Global.MemPoolService, Global.IndexDownloader);

				BuildAvaloniaApp()
					.StartShellApp<AppBuilder, MainWindow>("Wasabi Wallet", new DefaultLayoutFactory(), () => new MainWindowViewModel(statusBar));
			}
			catch (Exception ex)
			{
				Logger.LogCritical<Program>(ex);
			}
			finally
			{
				statusBar?.Dispose();
				Global.Dispose();
			}
		}

		private static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().UseReactiveUI();
	}
}
