﻿using NileLibraryNS.Models;
using NileLibraryNS.Services;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace NileLibraryNS
{
    [LoadPlugin]
    public class NileLibrary : LibraryPluginBase<NileLibrarySettingsViewModel>
    {
        public NileLibrary(IPlayniteAPI api) : base(
            "Nile (Amazon)",
            Guid.Parse("5901B4B4-774D-411A-9CCE-807C5CA49D88"),
            new LibraryPluginProperties { CanShutdownClient = true, HasSettings = true },
            new NileLibraryClient(),
            Nile.Icon,
            (_) => new NileLibrarySettingsView(),
            api)
        {
            SettingsViewModel = new NileLibrarySettingsViewModel(this, PlayniteApi);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            SettingsViewModel.IsFirstRunUse = firstRunSettings;
            return SettingsViewModel;
        }

        public override LibraryMetadataProvider GetMetadataDownloader()
        {
            return new AmazonGamesMetadataProvider();
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new NileInstallController(args.Game, this);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new NileUninstallController(args.Game, this);
        }

        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            var gameConfig = Nile.GetGameConfiguration(args.Game.InstallDirectory);
            if (Nile.GetGameRequiresClient(gameConfig) || !SettingsViewModel.Settings.StartGamesWithoutLauncher)
            {
                yield return new AutomaticPlayController(args.Game)
                {
                    Type = AutomaticPlayActionType.Url,
                    TrackingMode = TrackingMode.Directory,
                    Name = ResourceProvider.GetString(LOC.AmazonStartUsingClient).Format("Nile"),
                    TrackingPath = args.Game.InstallDirectory,
                    Path = $"amazon-games://play/{args.Game.GameId}"
                };
            }
            else
            {
                var controller = new AutomaticPlayController(args.Game)
                {
                    Type = AutomaticPlayActionType.File,
                    TrackingMode = TrackingMode.Directory,
                    Name = args.Game.Name,
                    TrackingPath = args.Game.InstallDirectory,
                    Path = Path.Combine(args.Game.InstallDirectory, gameConfig.Main.Command)
                };

                if (gameConfig.Main.Args.HasNonEmptyItems())
                {
                    controller.Arguments = string.Join(" ", gameConfig.Main.Args);
                }

                if (!gameConfig.Main.WorkingSubdirOverride.IsNullOrEmpty())
                {
                    controller.WorkingDir = Path.Combine(args.Game.InstallDirectory, gameConfig.Main.WorkingSubdirOverride);
                }
                else if (gameConfig.Main.Command.Contains("scummvm.exe", StringComparison.OrdinalIgnoreCase))
                {
                    // scummvm game have to have working directory set to games's install dir otherwise they won't start properly
                    controller.WorkingDir = args.Game.InstallDirectory;
                }

                yield return controller;
            }
        }

        internal Dictionary<string, GameMetadata> GetInstalledGames()
        {
            var games = new Dictionary<string, GameMetadata>();
            var installSqlPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Amazon Games\Data\Games\Sql\GameInstallInfo.sqlite");
            if (!File.Exists(installSqlPath))
            {
                Logger.Warn("Amazon games install game info file not found.");
                return games;
            }

            using (var sql = Playnite.SDK.Data.SQLite.OpenDatabase(installSqlPath, Playnite.SDK.Data.SqliteOpenFlags.ReadOnly))
            {
                foreach (var program in sql.Query<InstallGameInfo>(@"SELECT * FROM DbSet WHERE Installed = 1;"))
                {
                    if (!Directory.Exists(program.InstallDirectory))
                    {
                        continue;
                    }

                    var game = new GameMetadata()
                    {
                        InstallDirectory = Paths.FixSeparators(program.InstallDirectory),
                        GameId = program.Id,
                        Source = new MetadataNameProperty("Amazon"),
                        Name = program.ProductTitle.RemoveTrademarks(),
                        IsInstalled = true,
                        Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") }
                    };

                    games.Add(game.GameId, game);
                }
            }

            return games;
        }

        public List<GameMetadata> GetLibraryGames()
        {
            var games = new List<GameMetadata>();
            var client = new AmazonAccountClient(this);
            var entitlements = client.GetAccountEntitlements().GetAwaiter().GetResult();
            foreach (var item in entitlements)
            {
                if (item.product.productLine == "Twitch:FuelEntitlement")
                {
                    continue;
                }

                var game = new GameMetadata()
                {
                    Source = new MetadataNameProperty("Amazon"),
                    GameId = item.product.id,
                    Name = item.product.title.RemoveTrademarks(),
                    Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") }
                };

                games.Add(game);
            }

            return games;
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var allGames = new List<GameMetadata>();
            var installedGames = new Dictionary<string, GameMetadata>();
            Exception importError = null;

            if (SettingsViewModel.Settings.ImportInstalledGames)
            {
                try
                {
                    installedGames = GetInstalledGames();
                    Logger.Debug($"Found {installedGames.Count} installed Nile games.");
                    allGames.AddRange(installedGames.Values.ToList());
                }
                catch (Exception e) when (!PlayniteApi.ApplicationInfo.ThrowAllErrors)
                {
                    Logger.Error(e, "Failed to import installed Nile games.");
                    importError = e;
                }
            }

            if (SettingsViewModel.Settings.ConnectAccount)
            {
                try
                {
                    var libraryGames = GetLibraryGames();
                    Logger.Debug($"Found {libraryGames.Count} library Nile games.");

                    if (!SettingsViewModel.Settings.ImportUninstalledGames)
                    {
                        libraryGames = libraryGames.Where(lg => installedGames.ContainsKey(lg.GameId)).ToList();
                    }

                    foreach (var game in libraryGames)
                    {
                        if (installedGames.TryGetValue(game.GameId, out var installed))
                        {
                            installed.Playtime = game.Playtime;
                            installed.LastActivity = game.LastActivity;
                        }
                        else
                        {
                            allGames.Add(game);
                        }
                    }
                }
                catch (Exception e) when (!PlayniteApi.ApplicationInfo.ThrowAllErrors)
                {
                    Logger.Error(e, "Failed to import linked account Nile games details.");
                    importError = e;
                }
            }

            if (importError != null)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    ImportErrorMessageId,
                    string.Format(PlayniteApi.Resources.GetString("LOCLibraryImportError"), Name) +
                    System.Environment.NewLine + importError.Message,
                    NotificationType.Error,
                    () => OpenSettingsView()));
            }
            else
            {
                PlayniteApi.Notifications.Remove(ImportErrorMessageId);
            }

            return allGames;
        }
    }
}