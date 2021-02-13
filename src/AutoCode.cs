using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Game.Rust.Libraries;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Oxide.Plugins
{
  [Info("Auto Code", "slaymaster3000", "0.0.0-development")]
  [Description("Automatically sets the code on code locks placed.")]
  class AutoCode : RustPlugin
  {
    private AutoCodeConfig config;
    private Commands commands;
    private Data data;
    private Dictionary<BasePlayer, TempCodeLockInfo> tempCodeLocks;

    #region Hooks

    void Init()
    {
      config = new AutoCodeConfig(this);
      data = new Data(this);
      commands = new Commands(this);
      tempCodeLocks = new Dictionary<BasePlayer, TempCodeLockInfo>();

      config.Load();
      data.Load();
      Permissions.Register(this);
      commands.Register();
    }

    protected override void LoadDefaultConfig() {
      Interface.Oxide.LogInfo("New configuration file created.");
    }

    void OnServerSave()
    {
      RemoveAllTempCodeLocks();
      data.Save();
    }

    void Unload()
    {
      RemoveAllTempCodeLocks();
      data.Save();
    }

    object OnCodeEntered(CodeLock codeLock, BasePlayer player, string code)
    {
      // Not one of our temporary code locks?
      if (player == null || !tempCodeLocks.ContainsKey(player) || tempCodeLocks[player].CodeLock != codeLock)
      {
        UnsubscribeFromUnneedHooks();
        return null;
      }

      // Destroy the temporary code lock as soon as it's ok to do so.
      timer.In(0, () =>
      {
        DestoryTempCodeLock(player);
      });

      SetCode(player, code, tempCodeLocks[player].Guest);
      Effect.server.Run(codeLock.effectCodeChanged.resourcePath, player.transform.position);
      return false;
    }

    void OnEntitySpawned(CodeLock codeLock)
    {
      // Code already set?
      if (codeLock.hasCode && codeLock.hasGuestCode)
      {
        return;
      }

      BasePlayer player = BasePlayer.FindByID(codeLock.OwnerID);

      // No player or the player doesn't have permission?
      if (player == null || !permission.UserHasPermission(player.UserIDString, Permissions.Use))
      {
        return;
      }

      // No data for player.
      if (!data.Inst.playerSettings.ContainsKey(player.userID))
      {
        return;
      }

      Data.Structure.PlayerSettings settings = data.Inst.playerSettings[player.userID];

      // Player doesn't have a code?
      if (settings == null || settings.code == null)
      {
        return;
      }

      // Set the main code.
      codeLock.code = settings.code;
      codeLock.hasCode = true;
      codeLock.whitelistPlayers.Add(player.userID);

      // Set the guest code.
      if (settings.guestCode != null)
      {
        codeLock.guestCode = settings.guestCode;
        codeLock.hasGuestCode = true;
        codeLock.guestPlayers.Add(player.userID);
      }

      // Lock the lock.
      codeLock.SetFlag(BaseEntity.Flags.Locked, true);

      // Don't display code if in streamer mode.
      if (!player.net.connection.info.GetBool("global.streamermode"))
      {
        player.ChatMessage(
          string.Format(
            lang.GetMessage("CodeAutoLocked", this, player.UserIDString),
            settings.code
          )
        );
      }
    }

    object CanUseLockedEntity(BasePlayer player, CodeLock codeLock)
    {
      // Is a player that has permission and lock is locked?
      if (
        player != null &&
        codeLock.hasCode &&
        codeLock.HasFlag(BaseEntity.Flags.Locked) &&
        permission.UserHasPermission(player.UserIDString, Permissions.Use) &&
        permission.UserHasPermission(player.UserIDString, Permissions.Try) &&
        data.Inst.playerSettings.ContainsKey(player.userID)
      )
      {
        Data.Structure.PlayerSettings settings = data.Inst.playerSettings[player.userID];

        // Player has the code?
        if (settings != null && codeLock.code == settings.code)
        {
          // Auth the player.
          codeLock.whitelistPlayers.Add(player.userID);
        }
      }

      // Use default behavior.
      return null;
    }

    protected override void LoadDefaultMessages()
    {
      lang.RegisterMessages(new Dictionary<string, string>
        {
          { "NoPermission", "You don't have permission." },
          { "CodeAutoLocked", "Code lock placed with code {0}." },
          { "CodeUpdated", "Your new code is {0}." },
          { "GuestCodeUpdated", "Your new guest code is {0}." },
          { "CodeRemoved", "Your code has been removed." },
          { "GuestCodeRemoved", "Your guest code has been removed." },
          { "InvalidArgsTooMany", "Too many arguments supplied." },
          { "Info", "Code: {0}\nGuest Code: {1}\n\nUsage: {2}" },
          { "NotSet", "Not set." },
          { "SyntaxError", "Syntax Error: expected command in the form:\n{0}" },
          { "SpamPrevention", "Too many recent code sets. Please wait {0} and try again." },
          { "InvalidArguments", "Invalid arguments supplied." },
          { "ErrorNoPlayerFound", "Error: No player found." },
          { "ErrorMoreThanOnePlayerFound", "Error: More than one player found." },
          { "ResettingAllLockOuts", "Resetting lock outs for all players." },
          { "ResettingLockOut", "Resetting lock outs for {0}." },
        }, this);
    }

    #endregion

    #region API

    [ObsoleteAttribute("This method is deprecated. Call GetCode instead.", false)]
    public string GetPlayerCode(BasePlayer player) => GetCode(player);

    /// <summary>
    /// Get the code for the given player.
    /// </summary>
    /// <param name="player">The player to get the code for.</param>
    /// <param name="guest">If true, the guest code will be returned instead of the main code.</param>
    /// <returns>A string of the player's code or null if the player doesn't have a code.</returns>
    public string GetCode(BasePlayer player, bool guest = false)
    {
      if (data.Inst.playerSettings.ContainsKey(player.userID))
      {
        return guest
          ? data.Inst.playerSettings[player.userID].guestCode
          : data.Inst.playerSettings[player.userID].code;
      }

      return null;
    }

    /// <summary>
    /// Set the code for the given player.
    /// </summary>
    /// <param name="player">The player to set the code for.</param>
    /// <param name="code">The code to set for the given player.</param>
    /// <param name="guest">If true, the guest code will be set instead of the main code.</param>
    public void SetCode(BasePlayer player, string code, bool guest = false)
    {
      if (!data.Inst.playerSettings.ContainsKey(player.userID))
      {
        data.Inst.playerSettings.Add(player.userID, new Data.Structure.PlayerSettings());
      }

      // Load the player's settings
      Data.Structure.PlayerSettings settings = data.Inst.playerSettings[player.userID];

      double currentTime = Utils.CurrentTime();

      if (config.Options.SpamPrevention.Enabled)
      {
        double timePassed = currentTime - settings.lastSet;
        bool lockedOut = currentTime < settings.lockedOutUntil;
        double lockOutFor = config.Options.SpamPrevention.LockOutTime * Math.Pow(2, (config.Options.SpamPrevention.UseExponentialLockOutTime ? settings.lockedOutTimes : 0));

        if (!lockedOut)
        {
          // Called again within spam window time?
          if (timePassed < config.Options.SpamPrevention.WindowTime)
          {
            settings.timesSetInSpamWindow++;
          }
          else
          {
            settings.timesSetInSpamWindow = 1;
          }

          // Too many recent changes?
          if (settings.timesSetInSpamWindow > config.Options.SpamPrevention.Attempts)
          {
            // Locked them out.
            settings.lockedOutUntil = currentTime + lockOutFor;
            settings.lastLockedOut = currentTime;
            settings.lockedOutTimes++;
            settings.timesSetInSpamWindow = 0;
            lockedOut = true;
          }
        }

        // Locked out?
        if (lockedOut)
        {
          player.ChatMessage(
            string.Format(
              lang.GetMessage("SpamPrevention", this, player.UserIDString),
              TimeSpan.FromSeconds(Math.Ceiling(settings.lockedOutUntil - currentTime)).ToString(@"d\d\ h\h\ mm\m\ ss\s").TrimStart(' ', 'd', 'h', 'm', 's', '0'),
              config.Options.SpamPrevention.LockOutTime,
              config.Options.SpamPrevention.WindowTime
            )
          );
          return;
        }

        // Haven't been locked out for a long time?
        if (currentTime > settings.lastLockedOut + config.Options.SpamPrevention.LockOutResetFactor * lockOutFor)
        {
          // Reset their lockOuts.
          settings.lockedOutTimes = 0;
        }
      }

      if (guest)
      {
        settings.guestCode = code;
      }
      else
      {
        settings.code = code;
      }

      settings.lastSet = currentTime;

      // Don't display code if in streamer mode.
      if (!player.net.connection.info.GetBool("global.streamermode"))
      {
        player.ChatMessage(
          string.Format(
            lang.GetMessage(guest ? "GuestCodeUpdated" : "CodeUpdated", this, player.UserIDString),
            code
          )
        );
      }
    }

    /// <summary>
    /// This method will only toggle off, not on.
    /// </summary>
    /// <param name="player"></param>
    [ObsoleteAttribute("This method is deprecated.", true)]
    public void ToggleEnabled(BasePlayer player)
    {
      RemoveCode(player);
    }

    /// <summary>
    /// Remove the given player's the code.
    /// </summary>
    /// <param name="player">The player to remove the code of.</param>
    /// <param name="guest">If true, the guest code will be removed instead of the main code.</param>
    public void RemoveCode(BasePlayer player, bool guest = false)
    {
      if (!data.Inst.playerSettings.ContainsKey(player.userID))
      {
        return;
      }

      // Load the player's settings.
      Data.Structure.PlayerSettings settings = data.Inst.playerSettings[player.userID];

      if (!guest)
      {
        settings.code = null;
      }

      // Remove the guest code both then removing the main code and when just removing the guest code.
      settings.guestCode = null;

      player.ChatMessage(lang.GetMessage(guest ? "GuestCodeRemoved" : "CodeRemoved", this, player.UserIDString));
    }

    [ObsoleteAttribute("This method is deprecated. Call IsValidCode instead.", false)]
    public bool ValidCode(string codeString) => ValidCode(codeString);

    /// <summary>
    /// Is the given string a valid code?
    /// </summary>
    /// <param name="code">The code to test.</param>
    /// <returns>True if it's valid, otherwise false.</returns>
    public bool IsValidCode(string codeString)
    {
      if (codeString == null)
      {
        return false;
      }

      int code;
      if (codeString.Length == 4 && int.TryParse(codeString, out code))
      {
        if (code >= 0 && code < 10000)
        {
          return true;
        }
      }

      return false;
    }

    [ObsoleteAttribute("This method is deprecated. Call GenerateRandomCode instead.", false)]
    public static string GetRandomCode()
    {
      return Core.Random.Range(0, 10000).ToString("0000");
    }

    /// <summary>
    /// Generate a random code.
    /// </summary>
    /// <returns></returns>
    public string GenerateRandomCode()
    {
      return Core.Random.Range(0, 10000).ToString("0000");
    }

    /// <summary>
    /// Open the code lock UI for the given player.
    /// </summary>
    /// <param name="player">The player to open the lock UI for.</param>
    /// <param name="guest">If true, the guest code will be set instead of the main code.</param>
    public void OpenCodeLockUI(BasePlayer player, bool guest = false)
    {
      // Make sure any old code lock is destroyed.
      DestoryTempCodeLock(player);

      // Create a temporary code lock.
      CodeLock codeLock = GameManager.server.CreateEntity(
        "assets/prefabs/locks/keypad/lock.code.prefab",
        player.eyes.position + new Vector3(0, -3, 0)
      ) as CodeLock;

      // Creation failed? Exit.
      if (codeLock == null)
      {
        Interface.Oxide.LogError("Failed to create code lock.");
        return;
      }

      // Associate the lock with the player.
      tempCodeLocks.Add(player, new TempCodeLockInfo(codeLock, guest));

      // Spawn and lock the code lock.
      codeLock.Spawn();
      codeLock.SetFlag(BaseEntity.Flags.Locked, true);

      // Open the code lock UI.
      codeLock.ClientRPCPlayer(null, player, "EnterUnlockCode");

      // Listen for code lock codes.
      Subscribe("OnCodeEntered");

      // Destroy the temporary code lock in 20s.
      timer.In(20f, () =>
      {
        if (tempCodeLocks.ContainsKey(player) && tempCodeLocks[player].CodeLock == codeLock)
        {
          DestoryTempCodeLock(player);
        }
      });
    }

    /// <summary>
    /// Reset (remove) all lock outs caused by spam protection.
    /// </summary>
    public void ResetAllLockOuts()
    {
      foreach (ulong userID in data.Inst.playerSettings.Keys)
      {
        ResetLockOut(userID);
      }
    }


    /// <summary>
    /// Reset (remove) the lock out caused by spam protection for the given player.
    /// </summary>
    /// <param name="player"></param>
    public void ResetLockOut(BasePlayer player)
    {
      ResetLockOut(player.userID);
    }

    /// <summary>
    /// Reset (remove) the lock out caused by spam protection for the given user id.
    /// </summary>
    /// <param name="userID"></param>
    public void ResetLockOut(ulong userID)
    {
      if (!data.Inst.playerSettings.ContainsKey(userID))
      {
        return;
      }

      Data.Structure.PlayerSettings settings = data.Inst.playerSettings[userID];
      settings.lockedOutTimes = 0;
      settings.lockedOutUntil = 0;
    }

    #endregion

    /// <summary>
    /// Destroy the temporary code lock for the given player.
    /// </summary>
    /// <param name="player"></param>
    private void DestoryTempCodeLock(BasePlayer player)
    {
      // Code lock for player exists? Remove it.
      if (tempCodeLocks.ContainsKey(player))
      {
        // Code lock exists? Destroy it.
        if (!tempCodeLocks[player].CodeLock.IsDestroyed)
        {
          tempCodeLocks[player].CodeLock.Kill();
        }
        tempCodeLocks.Remove(player);
      }
      UnsubscribeFromUnneedHooks();
    }

    /// <summary>
    /// Remove all the temporary code locks.
    /// </summary>
    private void RemoveAllTempCodeLocks()
    {
      // Remove all temp code locks - we don't want to save them.
      foreach (TempCodeLockInfo codeLockInfo in tempCodeLocks.Values)
      {
        if (!codeLockInfo.CodeLock.IsDestroyed)
        {
          codeLockInfo.CodeLock.Kill();
        }
      }
      tempCodeLocks.Clear();
      UnsubscribeFromUnneedHooks();
    }

    /// <summary>
    /// Unsubscribe from things that there is not point currently being subscribed to.
    /// </summary>
    private void UnsubscribeFromUnneedHooks()
    {
      // No point listing for code lock codes if we aren't expecting any.
      if (tempCodeLocks.Count < 1)
      {
        Unsubscribe("OnCodeEntered");
      }
    }

    /// <summary>
    /// The Config for this plugin.
    /// </summary>
    private class AutoCodeConfig
    {
      // The plugin.
      private readonly AutoCode plugin;

      // The oxide DynamicConfigFile instance.
      public readonly DynamicConfigFile OxideConfig;

      // Meta.
      private bool UnsavedChanges = false;

      public AutoCodeConfig() { }

      public AutoCodeConfig(AutoCode plugin)
      {
        this.plugin = plugin;
        OxideConfig = plugin.Config;
      }

      public CommandsDef Commands = new CommandsDef();
      public class CommandsDef
      {
        public string Use = "code";
      };

      public OptionsDef Options = new OptionsDef();
      public class OptionsDef
      {
        public bool DisplayPermissionErrors = true;

        public SpamPreventionDef SpamPrevention = new SpamPreventionDef();
        public class SpamPreventionDef
        {
          public bool Enabled = true;
          public int Attempts = 5;
          public double LockOutTime = 5.0;
          public double WindowTime = 30.0;
          public bool UseExponentialLockOutTime = true;
          public double LockOutResetFactor = 5.0;
        };
      };

      /// <summary>
      /// Save the changes to the config file.
      /// </summary>
      public void Save(bool force = false)
      {
        if (UnsavedChanges || force)
        {
          plugin.SaveConfig();
        }
      }

      /// <summary>
      /// Load config values.
      /// </summary>
      public void Load()
      {
        // Options.
        Options.DisplayPermissionErrors = GetConfigValue(
          new string[] { "Options", "Display Permission Errors" },
          GetConfigValue(new string[] { "Options", "displayPermissionErrors" }, true, true)
        );
        RemoveConfigValue(new string[] { "Options", "displayPermissionErrors" }); // Remove deprecated version.

        // Spam prevention.
        Options.SpamPrevention.Enabled = GetConfigValue(new string[] { "Options", "Spam Prevention", "Enable" }, true);
        Options.SpamPrevention.Attempts = GetConfigValue(new string[] { "Options", "Spam Prevention", "Attempts" }, 5);
        Options.SpamPrevention.LockOutTime = GetConfigValue(new string[] { "Options", "Spam Prevention", "Lock Out Time" }, 5.0);
        Options.SpamPrevention.WindowTime = GetConfigValue(new string[] { "Options", "Spam Prevention", "Window Time" }, 30.0);
        Options.SpamPrevention.LockOutResetFactor = GetConfigValue(new string[] { "Options", "Spam Prevention", "Lock Out Reset Factor" }, 5.0);

        Options.SpamPrevention.UseExponentialLockOutTime = GetConfigValue(
          new string[] { "Options", "Spam Prevention", "Exponential Lock Out Time" },
          GetConfigValue(new string[] { "Options", "Spam Prevention", "Use Exponential Lock Out Time" }, true, true)
        );
        RemoveConfigValue(new string[] { "Options", "Spam Prevention", "Exponential Lock Out Time" }); // Remove deprecated version.

        // Commands.
        plugin.commands.Use = GetConfigValue(new string[] { "Commands", "Use" }, plugin.commands.Use);

        Save();
      }

      /// <summary>
      /// Get the config value for the given settings.
      /// </summary>
      private T GetConfigValue<T>(string[] settingPath, T defaultValue, bool deprecated = false)
      {
        object value = OxideConfig.Get(settingPath);
        if (value == null)
        {
          if (!deprecated)
          {
            SetConfigValue(settingPath, defaultValue);
          }
          return defaultValue;
        }

        return OxideConfig.ConvertValue<T>(value);
      }

      /// <summary>
      /// Set the config value for the given settings.
      /// </summary>
      private void SetConfigValue<T>(string[] settingPath, T newValue)
      {
        List<object> pathAndTrailingValue = new List<object>();
        foreach (var segment in settingPath)
        {
          pathAndTrailingValue.Add(segment);
        }
        pathAndTrailingValue.Add(newValue);

        OxideConfig.Set(pathAndTrailingValue.ToArray());
        UnsavedChanges = true;
      }

      /// <summary>
      /// Remove the config value for the given setting.
      /// </summary>
      private void RemoveConfigValue(string[] settingPath)
      {
        if (settingPath.Length == 1)
        {
          OxideConfig.Remove(settingPath[0]);
          return;
        }

        List<string> parentPath = new List<string>();
        for (int i = 0; i < settingPath.Length - 1; i++)
        {
          parentPath.Add(settingPath[i]);
        }

        Dictionary<string, object> parent = OxideConfig.Get(parentPath.ToArray()) as Dictionary<string, object>;
        parent.Remove(settingPath[settingPath.Length - 1]);
      }
    }

    /// <summary>
    /// Everything related to the data the plugin needs to save.
    /// </summary>
    private class Data
    {
      // The plugin.
      private readonly string Filename;

      // The actual data.
      public Structure Inst { private set; get; }

      public Data(AutoCode plugin)
      {
        Filename = plugin.Name;
      }

      /// <summary>
      /// Save the data.
      /// </summary>
      public void Save()
      {
        Interface.Oxide.DataFileSystem.WriteObject(Filename, Inst);
      }

      /// <summary>
      /// Load the data.
      /// </summary>
      public void Load()
      {
        Inst = Interface.Oxide.DataFileSystem.ReadObject<Structure>(Filename);
      }

      /// <summary>
      /// The data this plugin needs to save.
      /// </summary>
      public class Structure
      {
        public Dictionary<ulong, PlayerSettings> playerSettings = new Dictionary<ulong, PlayerSettings>();

        /// <summary>
        /// The settings saved for each player.
        /// </summary>
        public class PlayerSettings
        {
          public string code = null;
          public string guestCode = null;
          public double lastSet = 0;
          public int timesSetInSpamWindow = 0;
          public double lockedOutUntil = 0;
          public double lastLockedOut = 0;
          public int lockedOutTimes = 0;
        }
      }
    }

    /// <summary>
    /// The permissions this plugin uses.
    /// </summary>
    private static class Permissions
    {
      // Permissions.
      public const string Use = "autocode.use";
      public const string Try = "autocode.try";

      /// <summary>
      /// Register the permissions.
      /// </summary>
      public static void Register(AutoCode plugin)
      {
        plugin.permission.RegisterPermission(Use, plugin);
        plugin.permission.RegisterPermission(Try, plugin);
      }
    }

    /// <summary>
    /// Everything related to commands.
    /// </summary>
    private class Commands
    {
      // The plugin.
      private readonly AutoCode plugin;

      // The rust command instance.
      public readonly Command Rust;

      // Console Commands.
      public string ResetLockOut = "autocode.resetlockout";

      // Chat Commands.
      public string Use = "code";

      // Chat Command Arguments.
      public string Guest = "guest";
      public string PickCode = "pick";
      public string RandomCode = "random";
      public string RemoveCode = "remove";

      public Commands(AutoCode plugin)
      {
        this.plugin = plugin;
        Rust = plugin.cmd;
      }

      /// <summary>
      /// Register this command.
      /// </summary>
      public void Register()
      {
        Rust.AddConsoleCommand(ResetLockOut, plugin, HandleResetLockOut);
        Rust.AddChatCommand(Use, plugin, HandleUse);
      }

      /// <summary>
      /// Reset lock out.
      /// </summary>
      /// <returns></returns>
      private bool HandleResetLockOut(ConsoleSystem.Arg arg)
      {
        BasePlayer player = arg.Player();

        // Not admin?
        if (!arg.IsAdmin)
        {
          if (plugin.config.Options.DisplayPermissionErrors)
          {
            arg.ReplyWith(plugin.lang.GetMessage("NoPermission", plugin, player?.UserIDString));
          }

          return false;
        }

        // Incorrect number of args given.
        if (!arg.HasArgs(1) || arg.HasArgs(2))
        {
          arg.ReplyWith(plugin.lang.GetMessage("InvalidArguments", plugin, player?.UserIDString));
          return false;
        }

        string resetForString = arg.GetString(0).ToLower();

        // Reset all?
        if (resetForString == "*")
        {
          arg.ReplyWith(plugin.lang.GetMessage("ResettingAllLockOuts", plugin, player?.UserIDString));
          plugin.ResetAllLockOuts();
          return true;
        }

        // Find the player to reset for.
        List<BasePlayer> resetForList = new List<BasePlayer>();
        foreach (BasePlayer p in BasePlayer.allPlayerList)
        {
          if (p == null || string.IsNullOrEmpty(p.displayName))
          {
            continue;
          }

          if (p.UserIDString == resetForString || p.displayName.Contains(resetForString, CompareOptions.OrdinalIgnoreCase))
          {
            resetForList.Add(p);
          }
        }

        // No player found?
        if (resetForList.Count == 0)
        {
          arg.ReplyWith(plugin.lang.GetMessage("ErrorNoPlayerFound", plugin, player?.UserIDString));
          return false;
        }

        // Too many players found?
        if (resetForList.Count > 1)
        {
          arg.ReplyWith(plugin.lang.GetMessage("ErrorMoreThanOnePlayerFound", plugin, player?.UserIDString));
          return false;
        }

        // Rest for player.
        arg.ReplyWith(
          string.Format(
            plugin.lang.GetMessage("ResettingLockOut", plugin, player?.UserIDString),
            resetForList[0].displayName
          )
        );
        plugin.ResetLockOut(resetForList[0]);
        return true;
      }

      /// <summary>
      /// The "use" chat command.
      /// </summary>
      private void HandleUse(BasePlayer player, string label, string[] args)
      {
        // Allowed to use this command?
        if (!plugin.permission.UserHasPermission(player.UserIDString, Permissions.Use))
        {
          if (plugin.config.Options.DisplayPermissionErrors)
          {
            player.ChatMessage(plugin.lang.GetMessage("NoPermission", plugin, player.UserIDString));
          }
          return;
        }

        if (args.Length == 0)
        {
          ShowInfo(player, label, args);
          return;
        }

        // Create settings for user if they don't already have any settings.
        if (!plugin.data.Inst.playerSettings.ContainsKey(player.userID))
        {
          plugin.data.Inst.playerSettings.Add(player.userID, new Data.Structure.PlayerSettings());
        }

        string operation = args[0].ToLower();
        bool guest = false;

        if (operation == Guest)
        {
          if (args.Length < 2)
          {
            SyntaxError(player, label, args);
            return;
          }

          guest = true;
          operation = args[1].ToLower();
        }

        // Pick code.
        if (operation == PickCode)
        {
          if ((guest && args.Length > 2) || (!guest && args.Length > 1))
          {
            player.ChatMessage(string.Format(plugin.lang.GetMessage("InvalidArgsTooMany", plugin, player.UserIDString), label));
            return;
          }

          plugin.OpenCodeLockUI(player, guest);
          return;
        }

        // Remove?
        if (operation == RemoveCode)
        {
          if ((guest && args.Length > 2) || (!guest && args.Length > 1))
          {
            player.ChatMessage(string.Format(plugin.lang.GetMessage("InvalidArgsTooMany", plugin, player.UserIDString), label));
            return;
          }

          plugin.RemoveCode(player, guest);
          return;
        }

        // Use random code?
        if (operation == RandomCode)
        {
          if ((guest && args.Length > 2) || (!guest && args.Length > 1))
          {
            player.ChatMessage(string.Format(plugin.lang.GetMessage("InvalidArgsTooMany", plugin, player.UserIDString), label));
            return;
          }

          plugin.SetCode(player, plugin.GenerateRandomCode(), guest);
          return;
        }

        // Use given code?
        if (plugin.IsValidCode(operation))
        {
          if ((guest && args.Length > 2) || (!guest && args.Length > 1))
          {
            player.ChatMessage(string.Format(plugin.lang.GetMessage("InvalidArgsTooMany", plugin, player.UserIDString), label));
            return;
          }

          plugin.SetCode(player, operation, guest);
          return;
        }

        SyntaxError(player, label, args);
      }

      /// <summary>
      /// Show the player their info.
      /// </summary>
      private void ShowInfo(BasePlayer player, string label, string[] args)
      {
        string code = null;
        string guestCode = null;

        if (plugin.data.Inst.playerSettings.ContainsKey(player.userID))
        {
          Data.Structure.PlayerSettings settings = plugin.data.Inst.playerSettings[player.userID];
          code = settings.code;
          guestCode = settings.guestCode;

          // Hide codes for those in streamer mode.
          if (player.net.connection.info.GetBool("global.streamermode"))
          {
            code = code == null ? null : "****";
            guestCode = guestCode == null ? null : "****";
          }
        }

        player.ChatMessage(
          string.Format(
            plugin.lang.GetMessage("Info", plugin, player.UserIDString),
            code ?? plugin.lang.GetMessage("NotSet", plugin, player.UserIDString),
            guestCode ?? plugin.lang.GetMessage("NotSet", plugin, player.UserIDString),
            UsageInfo(label)
          )
        );
      }

      /// <summary>
      /// Notify the player that they entered a syntax error in their "use" chat command.
      /// </summary>
      private void SyntaxError(BasePlayer player, string label, string[] args)
      {
        player.ChatMessage(
          string.Format(
            plugin.lang.GetMessage("SyntaxError", plugin, player.UserIDString),
            UsageInfo(label)
          )
        );
      }

      /// <summary>
      /// Show how to use the "use" command.
      /// </summary>
      /// <returns></returns>
      private string UsageInfo(string label)
      {
        return string.Format("/{0} {1}", label, HelpGetAllUseCommandArguments());
      }

      /// <summary>
      /// Get all the arguments that can be supplied to the "use" command.
      /// </summary>
      /// <returns></returns>
      private string HelpGetAllUseCommandArguments()
      {
        return string.Format("[{0}] {1}", Guest, string.Join("|", new string[] { "1234", RandomCode, PickCode, RemoveCode }));
      }
    }

    /// <summary>
    /// Utility functions.
    /// </summary>
    private static class Utils
    {
      /// <summary>
      /// Get the current time.
      /// </summary>
      /// <returns>The number of seconds that have passed since 1970-01-01.</returns>
      public static double CurrentTime() => DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds;
    }

    /// <summary>
    /// The data stored for temp code locks.
    /// </summary>
    private class TempCodeLockInfo
    {
      public readonly CodeLock CodeLock;
      public readonly bool Guest;

      public TempCodeLockInfo(CodeLock CodeLock, bool Guest = false)
      {
        this.CodeLock = CodeLock;
        this.Guest = Guest;
      }
    }
  }
}
