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

namespace LatihasKTG;

[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public sealed class Plugin : IDalamudPlugin {
	internal const uint Territory = 1359;
	internal static AnswerOptions Options = new();

	internal sealed record WordTarget(string Key, string Text);

	internal sealed record AnswerOptions(int StableMs = 150, int FillDelayMs = 750, int IntervalMs = 750, int MaxAttemptsPerWord = 5) {
		public AnswerOptions Clamp() => new(Math.Clamp(StableMs, 50, 2000), Math.Clamp(FillDelayMs, 0, 60000), Math.Clamp(IntervalMs, 0, 60000), Math.Clamp(MaxAttemptsPerWord, 1, 100));
	}

	[PluginService] internal static IDalamudPluginInterface pluginInterface { get; set; } = null!;
	[PluginService] internal static IFramework framework { get; set; } = null!;
	[PluginService] internal static IClientState clientState { get; set; } = null!;
	[PluginService] internal static IPluginLog log { get; set; } = null!;
	[PluginService] internal static IGameGui gameGui { get; set; } = null!;
	[PluginService] internal static IGameInteropProvider GameInteropProvider { get; set; } = null!;
	[PluginService] internal static IObjectTable ObjectTable { get; set; } = null!;

	internal static Plugin Instance = null!;
	private static readonly AnswerController controller = new();
	private readonly MainWindow mainWindow;
	private static readonly ConcurrentQueue<Action> requests = new();
	private static long nextTick;
	internal static bool PreviewReady;
	internal static string PreviewReason = "尚未读取游戏 UI";
	internal static string PreviewInput = "";
	internal static List<WordTarget> PreviewTargets = [];
	internal static bool Running => controller.Running;
	internal static string Status => controller.Status;
	internal static string LastWord => controller.LastWord;
	internal static int Submissions => controller.Submissions;
	internal readonly WindowSystem WindowSystem = new("LatihasMomo");
	internal readonly Hook<GameMain.Delegates.ExecuteCommand> ExecuteCommandHook;

	public Plugin() {
		Instance = this;
		Options = Options.Clamp();
		WindowSystem.AddWindow(mainWindow = new MainWindow());
		pluginInterface.UiBuilder.Draw += WindowSystem.Draw;
		pluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
		pluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
		framework.Update += OnUpdate;
		mainWindow.IsOpen = true;
		ExecuteCommandHook = GameInteropProvider.HookFromAddress(GameMain.Addresses.ExecuteCommand.Value, (GameMain.Delegates.ExecuteCommand)ExecuteCommandDetour);
		if (disableLeaveDuty) ExecuteCommandHook.Enable();
		clientState.TerritoryChanged += TerritoryChanged;
		framework.Update += Update;
	}

	private static long dialogueClickAt;
	private static long dialogueClickDeadline;

	private unsafe void Update(IFramework _) {
		var now = Environment.TickCount64;
		if (now >= dialogueClickAt && now <= dialogueClickDeadline) {
			if (ClickAddonButton("KTGEnterDialogue", 9)) {
				dialogueClickAt = 0;
				dialogueClickDeadline = 0;
			}
		}

		if (clientState.TerritoryType != Territory) return;
		if (!Running) mainWindow.TryStart();
	}

	private static void TerritoryChanged(uint id) {
		if (infiniteMode && clientState is { IsLoggedIn: true, TerritoryType: 1360 }) {
			_ = framework.RunOnTick(RequestRace, delay: TimeSpan.FromSeconds(5));
		}
	}

	internal static (int, int, int, int)? LastLeaveDutyParam;
	internal static bool disableLeaveDuty = true;
	internal static bool infiniteMode = true;

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
		if (disableLeaveDuty && command == 819 && clientState.TerritoryType == Territory) {
			log.Warning($"[DisableLeaveDuty] {command},{param1},{param2},{param3},{param4}");
			LastLeaveDutyParam = (param1, param2, param3, param4);
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
				}
				controller.Start(Environment.TickCount64, Options);
			}
		});
	}

	internal static void Stop() {
		requests.Enqueue(() => {
			controller.Stop();
		});
	}

	internal static void SetOptions(AnswerOptions options) {
		requests.Enqueue(() => {
			controller.Stop("参数已修改，请重新启动");
			Options = options.Clamp();
		});
	}

	private void OnUpdate(IFramework frameworkUpdate) {
		try {
			while (requests.TryDequeue(out var action)) action();
			var now = Environment.TickCount64;
			if (now >= nextTick) {
				nextTick = now + 50;
				controller.Tick(now);
				PreviewReady = GameUiPort.Read(out _, out var input, out var targets, out var reason, out _);
				PreviewInput = input;
				PreviewTargets = targets;
				PreviewReason = reason;
			}
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
		ExecuteCommandHook.Disable();
		ExecuteCommandHook.Dispose();
	}
}
