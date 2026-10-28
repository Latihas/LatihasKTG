using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static LatihasKTG.Plugin;

namespace LatihasKTG;

internal static unsafe class GameUiPort {
	private const uint InputNodeId = 7;
	private const uint WordNodeId = 20;
	private const int WordCount = 20;

	internal static bool Read(out string session, out string inputText, out List<WordTarget> targets, out string reason, out bool retryable) {
		session = "";
		inputText = "";
		targets = [];
		reason = "";
		retryable = false;

		if (!clientState.IsLoggedIn) {
			reason = "角色未登录，已停止";
			return false;
		}
		if (gameGui.GameUiHidden) {
			reason = "游戏 UI 已隐藏，已停止";
			return false;
		}
		if (clientState.TerritoryType != Territory) {
			reason = $"当前区域 {clientState.TerritoryType} 与魔光键影区域 {Territory} 不一致";
			return false;
		}

		var inputAddon = GetAddon("KTGInput");
		var input = GetInput(inputAddon);
		if (input == null || !input->Enabled) {
			reason = "输入控件不存在、不可见或已禁用";
			retryable = true;
			return false;
		}
		var wordAddon = GetAddon("KTGEWord");
		if (wordAddon == null) {
			reason = "词条控件不存在、不可见或未就绪";
			retryable = true;
			return false;
		}
		for (var slot = WordCount - 1; slot >= 0; slot--) {
			var word = GetWord(wordAddon, slot);
			if (word == null) continue;

			var text = ReadText(word);
			var target = new WordTarget($"KTGEWord[1]/{WordContainerId(slot)}/{WordNodeId}@{(nint)word:X}", text);
			if (AnswerController.IsValid(target)) targets.Add(target);
		}

		session = $"{clientState.TerritoryType}/KTGInput:{(nint)inputAddon:X}/{(nint)input:X}";
		inputText = input->RawString.ToString();
		return true;
	}

	internal static bool Fill(WordTarget target, out string error, out bool retryable) {
		error = "";
		retryable = false;

		var inputAddon = GetAddon("KTGInput");
		var input = GetInput(inputAddon);
		if (input == null || !input->Enabled || input->OwnerAddon != inputAddon || input->Callback == null) {
			error = "输入控件不存在、不可见或已禁用";
			retryable = true;
			return false;
		}

		input->GetFocusNode();
		input->SetText(target.Text);
		return true;
	}

	internal static bool Submit(string session, WordTarget target, out string error, out bool retryable) {
		error = "";
		retryable = false;

		var inputAddon = GetAddon("KTGInput");
		var input = GetInput(inputAddon);
		if (input == null || !input->Enabled) {
			error = "输入控件不存在、不可见或已禁用";
			retryable = true;
			return false;
		}
		if (input->OwnerAddon != inputAddon || input->Callback == null) {
			error = "提交回调已失效";
			retryable = true;
			return false;
		}

		var bytes = Encoding.UTF8.GetBytes(target.Text + "\0");
		var evaluated = Encoding.UTF8.GetBytes(target.Text + "\0");
		InputCallbackResult result;
		fixed (byte* rawPtr = bytes) {
			fixed (byte* evaluatedPtr = evaluated) {
				result = input->Callback(inputAddon, InputCallbackType.Enter, rawPtr, evaluatedPtr, input->CallbackEventKind);
			}
		}

		switch (result) {
			case InputCallbackResult.ClearText:
				ClearOwnedInput(session, target.Text);
				break;
			case InputCallbackResult.Unknown2:
			case InputCallbackResult.None:
				break;
			default:
				error = $"输入回调返回了不支持的结果 {result}，已停止";
				return false;
		}
		return true;
	}

	internal static void ClearOwnedInput(string session, string text) {
		var inputAddon = GetAddon("KTGInput");
		var input = GetInput(inputAddon);
		if (input == null || input->RawString.ToString() != text) return;

		var currentSession = $"{clientState.TerritoryType}/KTGInput:{(nint)inputAddon:X}/{(nint)input:X}";
		if (currentSession == session) input->SetText("");
	}

	private static AtkUnitBase* GetAddon(string name, int index = 1) {
		var addon = gameGui.GetAddonByName(name, index);
		if (addon == IntPtr.Zero) return null;

		var address = (AtkUnitBase*)addon.Address;
		return address != null && address->IsReady && address->IsVisible ? address : null;
	}

	private static AtkComponentTextInput* GetInput(AtkUnitBase* addon) {
		if (addon == null) return null;

		var node = addon->GetNodeById(InputNodeId);
		if (node == null || !IsShown(node)) return null;
		return AsInput(node);
	}

	private static AtkTextNode* GetWord(AtkUnitBase* addon, int slot) {
		if (addon == null) return null;

		var container = addon->GetNodeById(WordContainerId(slot));
		if (container == null || !IsShown(container)) return null;

		var componentNode = container->GetAsAtkComponentNode();
		if (componentNode == null || componentNode->Component == null) return null;

		var node = componentNode->Component->GetNodeById(WordNodeId);
		if (node == null || node->Type != NodeType.Text || !IsShown(node)) return null;
		return node->GetAsAtkTextNode();
	}

	private static uint WordContainerId(int slot) => slot == 0 ? 2u : (uint)(20000 + slot);

	private static AtkComponentTextInput* AsInput(AtkResNode* node) {
		if (node == null || (int)node->Type < 1000) return null;

		var component = ((AtkComponentNode*)node)->Component;
		if (component == null || component->GetComponentType() != ComponentType.TextInput) return null;
		return (AtkComponentTextInput*)component;
	}

	private static bool IsShown(AtkResNode* node) {
		var remaining = 128;
		for (var current = node; current != null; current = current->ParentNode) {
			if (--remaining == 0 || !current->IsVisible() || current->IsDrawDisabled || current->Color.A == 0) return false;
		}
		return true;
	}

	private static string ReadText(AtkTextNode* node) => SeString.Parse(node->NodeText.AsSpan()).TextValue;
}
