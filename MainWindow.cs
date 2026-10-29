using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;
using static LatihasKTG.Plugin;

namespace LatihasKTG;

internal sealed class MainWindow() : Window("LatihasKTG") {
	private bool delaysInitialized;
	private int intervalMs;

	internal void TryStart() {
		ClampDelayInputs();
		Start(intervalMs);
	}

	public override void Draw() {
		if (!delaysInitialized) {
			intervalMs = Options.IntervalMs;
			delaysInitialized = true;
		}
		ImGui.TextUnformatted(Plugin.Status);
		ImGui.BeginDisabled(Running);
		if (ImGui.Button("开始", new Vector2(100f, 0f))) {
			TryStart();
		}
		ImGui.EndDisabled();
		ImGui.SameLine();
		if (ImGui.Button("停止", new Vector2(100f, 0f))) {
			Stop();
		}
		ImGui.TextUnformatted($"提交调用：{Submissions}    最近词条：{LastWord}");

		if (ImGui.Checkbox("禁用LeaveDuty", ref disableLeaveDuty)) {
			if (disableLeaveDuty) Instance.ExecuteCommandHook.Enable();
			else Instance.ExecuteCommandHook.Disable();
			SaveConfiguration();
		}
		ImGui.SameLine();
		if (ImGui.Button("强制LeaveDuty")) {
			Instance.ExecuteCommandHook.Original(819, 0, 0, 0, 0);
		}
		if (ImGui.Checkbox("全自动(需要先到柜台前)", ref infiniteMode)) {
			if (!infiniteMode) autoPurchase = false;
			SaveConfiguration();
		}
		ImGui.SameLine();
		if (ImGui.Button("立刻排本") && clientState.TerritoryType == 1360) {
			disableLeaveDuty = true;
			Instance.ExecuteCommandHook.Enable();
			SaveConfiguration();
			BeginLobbyCycle();
		}
		if (infiniteMode && ImGui.Checkbox("自动购买", ref autoPurchase)) {
			SaveConfiguration();
			if (autoPurchase && clientState.TerritoryType == 1360) BeginLobbyCycle();
		}
		if (ExchangeStatus.Length > 0) ImGui.TextWrapped(ExchangeStatus);

		ImGui.SetNextItemWidth(160f);
		ImGui.InputInt("答题间隔（ms）(0)", ref intervalMs);
		if (ImGui.Button("保存延迟")) {
			ClampDelayInputs();
			SetOptions(Options with {
				IntervalMs = intervalMs
			});
		}
		ImGui.SameLine();
		ImGui.TextUnformatted($"已保存：输入提交无延迟 / 间隔 {Options.IntervalMs} ms");
		ImGui.Separator();
		if (!PreviewReady) {
			ImGui.TextWrapped($"当前不可输入：{PreviewReason}");
		} else {
			ImGui.TextUnformatted($"输入内容：{PreviewInput}");
			ImGui.TextWrapped($"可见目标：{string.Join(" | ", PreviewTargets.Select(t => t.Text))}");
		}
		var stable = Options.StableMs;
		if (ImGui.InputInt("词条稳定时间（毫秒）(0)", ref stable)) {
			SetOptions(Options with {
				StableMs = stable
			});
		}
	}

	private void ClampDelayInputs() => intervalMs = Math.Clamp(intervalMs, 0, 60000);
}
