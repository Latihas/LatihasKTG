using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Action = System.Action;

namespace LatihasKTG;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public sealed class Plugin : IDalamudPlugin {
	internal const uint Territory = 1359;
	internal static AnswerOptions Options = new();
	internal static Configuration Configuration { get; private set; } = null!;

	internal sealed record WordTarget(string Key, string Text);

	internal sealed record AnswerOptions(int StableMs = 150, int FillDelayMs = 750, int IntervalMs = 750, int MaxAttemptsPerWord = 5) {
		public AnswerOptions Clamp() => new(Math.Clamp(StableMs, 0, 2000), Math.Clamp(FillDelayMs, 0, 60000), Math.Clamp(IntervalMs, 0, 60000), Math.Clamp(MaxAttemptsPerWord, 1, 100));
	}

	[PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
	[PluginService] internal static IDalamudPluginInterface pluginInterface { get; set; } = null!;
	[PluginService] internal static IFramework framework { get; set; } = null!;
	[PluginService] internal static IClientState clientState { get; set; } = null!;
	[PluginService] internal static IPluginLog log { get; set; } = null!;
	[PluginService] internal static IGameGui gameGui { get; set; } = null!;
	[PluginService] internal static IGameInteropProvider GameInteropProvider { get; set; } = null!;
	[PluginService] internal static IObjectTable ObjectTable { get; set; } = null!;
	[PluginService] internal static ICondition Condition { get; set; } = null!;
	[PluginService] internal static ITargetManager TargetManager { get; set; } = null!;
	[PluginService] internal static IDataManager DataManager { get; set; } = null!;

	internal static Plugin Instance = null!;
	private static readonly AnswerController controller = new();
	private readonly MainWindow mainWindow;
	private readonly ChestExchangeAutomation chestExchange;
	private static readonly ConcurrentQueue<Action> requests = new();
	internal static bool PreviewReady;
	internal static string PreviewReason = "尚未读取游戏 UI";
	internal static string PreviewInput = "";
	internal static List<WordTarget> PreviewTargets = [];
	internal static bool Running => controller.Running;
	internal static string Status => controller.Status;
	internal static string LastWord => controller.LastWord;
	internal static int Submissions => controller.Submissions;
	internal readonly WindowSystem WindowSystem = new("LatihasKTG");
	internal readonly Hook<GameMain.Delegates.ExecuteCommand> ExecuteCommandHook;

	public Plugin() {
		Instance = this;
		Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
		Options = new AnswerOptions(
			Configuration.StableMs,
			Configuration.FillDelayMs,
			Configuration.IntervalMs,
			Configuration.MaxAttemptsPerWord).Clamp();
		disableLeaveDuty = Configuration.DisableLeaveDuty;
		infiniteMode = Configuration.InfiniteMode;
		autoPurchase = Configuration.AutoPurchase;
		SaveConfiguration();
		chestExchange = new ChestExchangeAutomation();
		WindowSystem.AddWindow(mainWindow = new MainWindow());
		pluginInterface.UiBuilder.Draw += WindowSystem.Draw;
		pluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
		pluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
		framework.Update += OnUpdate;
		ExecuteCommandHook = GameInteropProvider.HookFromAddress(GameMain.Addresses.ExecuteCommand.Value, (GameMain.Delegates.ExecuteCommand)ExecuteCommandDetour);
		if (disableLeaveDuty) ExecuteCommandHook.Enable();
		clientState.TerritoryChanged += TerritoryChanged;
		framework.Update += Update;
	}


	private static long dialogueClickAt;
	private static long dialogueClickDeadline;
	// private DateTime lastQuit = DateTime.MinValue;

	private void Update(IFramework _) {
		// if (clientState.TerritoryType == Territory) {
		// 	var nowx = DateTime.Now;
		// 	if (gameGui.GetAddonByName("KTGResult").IsVisible && (nowx - lastQuit).TotalSeconds > 5) {
		// 		Instance.ExecuteCommandHook.Original(819, 0, 0, 0, 0);
		// 		lastQuit = nowx;
		// 	}
		// }
		var now = Environment.TickCount64;
		chestExchange.Tick(now, infiniteMode && autoPurchase, infiniteMode);
		if (now >= dialogueClickAt && now <= dialogueClickDeadline) {
			if (ClickAddonButton("KTGEnterDialogue", 9)) {
				dialogueClickAt = 0;
				dialogueClickDeadline = 0;
			}
		}

		if (clientState.TerritoryType != Territory) return;
		if (infiniteMode && !Running) mainWindow.TryStart();
	}

	private void TerritoryChanged(uint id) {
		if (id == Territory) mainWindow.IsOpen = true;
		if (infiniteMode && clientState is { IsLoggedIn: true, TerritoryType: 1360 }) {
			_ = framework.RunOnTick(BeginLobbyCycle, TimeSpan.FromSeconds(5));
		}
	}

	internal static bool disableLeaveDuty = true;
	internal static bool infiniteMode = true;
	internal static bool autoPurchase;
	internal static string ExchangeStatus => Instance?.chestExchange.Status ?? "";

	internal static void SaveConfiguration() {
		if (Configuration is null) return;
		Configuration.StableMs = Options.StableMs;
		Configuration.FillDelayMs = Options.FillDelayMs;
		Configuration.IntervalMs = Options.IntervalMs;
		Configuration.MaxAttemptsPerWord = Options.MaxAttemptsPerWord;
		Configuration.DisableLeaveDuty = disableLeaveDuty;
		Configuration.InfiniteMode = infiniteMode;
		Configuration.AutoPurchase = autoPurchase;
		Configuration.Save();
	}

	internal static void BeginLobbyCycle() {
		if (infiniteMode && autoPurchase) Instance.chestExchange.Begin(Environment.TickCount64);
		else RequestRace();
	}

	internal static void RequestRace() {
		requests.Enqueue(RequestRaceOnFrameworkThread);
	}

	private static unsafe void RequestRaceOnFrameworkThread() {
		foreach (var obj in ObjectTable.StandObjects) {
			if (obj.Name.ToString() != "报名登记员") continue;
			var target = (GameObject*)obj.Address;
			TargetSystem.Instance()->Target = target;
			var interactionResult = TargetSystem.Instance()->InteractWithObject(target, false);
			if (interactionResult == 0) log.Debug("立刻排本：交互返回 0，但继续等待对话窗口");

			var now = Environment.TickCount64;
			dialogueClickAt = now + 500;
			dialogueClickDeadline = now + 15000;
			return;
		}
		dialogueClickAt = 0;
		dialogueClickDeadline = 0;
		log.Warning("立刻排本：当前对象表中未找到报名登记员");
	}

	private static unsafe bool ClickAddonButton(string name, uint buttonId) {
		var addon = GetAddon(name);
		if (addon == null) return false;
		var target = addon->GetComponentButtonById(buttonId);
		if (target == null || target->AtkResNode == null || !target->IsEnabled || !target->AtkResNode->IsVisible()) return false;
		Click(target, addon);
		return true;
	}

	private static unsafe void Click(AtkComponentButton* target, AtkUnitBase* addon) {
		var btnRes = target->AtkComponentBase.OwnerNode->AtkResNode;
		var evt = btnRes.AtkEventManager.Event;
		addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, btnRes.AtkEventManager.Event);
		var resetEvt = btnRes.AtkEventManager.Event;
		resetEvt->State.StateFlags = AtkEventStateFlags.None;
		addon->ReceiveEvent(resetEvt->State.EventType, (int)resetEvt->Param, btnRes.AtkEventManager.Event);
	}

	private static unsafe AtkUnitBase* GetAddon(string name, int index = 1) {
		var addon = gameGui.GetAddonByName(name, index);
		if (addon == IntPtr.Zero) return null;

		var address = (AtkUnitBase*)addon.Address;
		return address != null && address->IsReady && address->IsVisible ? address : null;
	}

	private bool ExecuteCommandDetour(int command, int param1, int param2, int param3, int param4) {
		if (disableLeaveDuty && command == 819 && param1 == 1 && param2 == 0 && param3 == 0 && param4 == 0 && clientState.TerritoryType == Territory) {
			log.Warning($"[DisableLeaveDuty] {command},{param1},{param2},{param3},{param4}");
			return true;
		}
		return ExecuteCommandHook.Original(command, param1, param2, param3, param4);
	}

	internal static void Start(int? operationDelayMs = null, int? intervalMs = null) {
		requests.Enqueue(() => {
			if (!controller.Running) {
				if (operationDelayMs.HasValue || intervalMs.HasValue) {
					Options = (Options with {
						FillDelayMs = operationDelayMs ?? Options.FillDelayMs,
						IntervalMs = intervalMs ?? Options.IntervalMs
					}).Clamp();
					SaveConfiguration();
				}
				controller.Start(Environment.TickCount64, Options);
			}
		});
	}

	internal static void Stop() {
		infiniteMode = false;
		autoPurchase = false;
		SaveConfiguration();
		requests.Enqueue(() => {
			controller.Stop();
			Instance.chestExchange.Stop("已停止");
		});
	}

	internal static void SetOptions(AnswerOptions options) {
		requests.Enqueue(() => {
			controller.Stop("参数已修改，请重新启动");
			Options = options.Clamp();
			SaveConfiguration();
		});
	}

	private void OnUpdate(IFramework frameworkUpdate) {
		try {
			while (requests.TryDequeue(out var action)) action();
			var now = Environment.TickCount64;
			controller.Tick(now);
			PreviewReady = GameUiPort.Read(out _, out var input, out var targets, out var reason, out _);
			PreviewInput = input;
			PreviewTargets = targets;
			PreviewReason = reason;
		} catch (Exception ex) {
			controller.Stop("读取或输入异常，已停止；详情见 /xllog");
			log.Error(ex, "FFMoGuang update failed");
			PreviewReady = false;
			PreviewReason = controller.Status;
			PreviewInput = "";
			PreviewTargets = [];
		}
	}

	public void Dispose() {
		framework.Update -= OnUpdate;
		framework.Update -= Update;
		pluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
		pluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
		pluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
		controller.Stop();
		SaveConfiguration();
		chestExchange.Dispose();
		ExecuteCommandHook.Disable();
		ExecuteCommandHook.Dispose();
	}
}