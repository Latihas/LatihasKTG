using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace LatihasKTG;

internal sealed unsafe class ChestExchangeAutomation : IDisposable {
	private enum Stage {
		Idle,
		Check,
		MoveToExchange,
		OpenShop,
		WaitShop,
		WaitCategory,
		Buy,
		Receipt,
		CloseShop,
		ReturnToRegistration,
		CheckReturn,
		Failed
	}

	private const uint ExchangeNpcId = 1058753;
	private const uint RegistrationNpcId = 1058751;
	private const uint CurrencyItemId = 52322;
	private const uint ChestItemId = 52450;
	private const int ChestCost = 910;
	private const int BatchSize = 9;
	private const int BagStackSize = 999;
	private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
	private static readonly string[] BlockingAddons = ["SelectString", "SelectIconString", "Talk", "InputNumeric", "ContextMenu", "Trade", "ShopExchangeItemDialog"];

	private Stage stage;
	private long deadline;
	private long settleUntil;
	private ushort shopId;
	private int beforePoints;
	private int beforeChests;
	private bool confirmed;
	private bool cancelled;
	private bool closeSent;
	private bool yieldedToEntry;
	private string status = "自动购买未运行";

	internal string Status => status;
	private bool Active => stage is not Stage.Idle and not Stage.Failed;

	internal void Begin(long now) {
		if (Active) return;
		stage = Stage.Check;
		deadline = now + 20000;
		settleUntil = 0;
		shopId = 0;
		confirmed = cancelled = closeSent = yieldedToEntry = false;
		status = "检查金碟点和背包";
	}

	internal void Stop(string message) {
		stage = Stage.Idle;
		deadline = 0;
		settleUntil = 0;
		shopId = 0;
		shopItemSlot = -1;
		confirmed = cancelled = closeSent = yieldedToEntry = false;
		status = message;
	}

	internal void Tick(long now, bool autoPurchaseEnabled, bool autoQueueEnabled) {
		if (!Active) return;
		if (!Plugin.clientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null) {
			status = "等待角色状态恢复";
			return;
		}
		if (Plugin.clientState.TerritoryType != 1360) {
			Fail("兑换尚未核实便离开大厅，请手动核对点数和宝箱");
			return;
		}
		if (now >= deadline) {
			Fail(stage == Stage.Receipt ? "未能同时确认点数扣除和宝箱到账，不会重复下单" : "兑换流程等待超时");
			return;
		}
		if (!CanAct()) {
			status = "等待游戏状态或交互窗口恢复";
			return;
		}
		if (now < settleUntil) return;

		var entryPriority = HasEntryPriority();
		if (entryPriority && stage != Stage.Receipt) {
			yieldedToEntry = true;
			status = "检测到排队或进入确认，优先进本";
			if (Visible("ShopExchangeCurrency") != null) {
				if (!VerifyShopIdentity(Visible("ShopExchangeCurrency"))) {
					Fail("进本期间兑换商店身份校验失败，已保留窗口");
					return;
				}
				shopId = Visible("ShopExchangeCurrency")->Id;
				stage = Stage.CloseShop;
				closeSent = false;
			} else {
				Finish(false, "已优先进本，兑换留到下次返回大厅");
			}
			return;
		}

		if (!autoPurchaseEnabled && stage != Stage.Receipt) {
			status = "自动购买已关闭，返回报名点";
			if (Visible("ShopExchangeCurrency") != null) {
				if (!VerifyShopIdentity(Visible("ShopExchangeCurrency"))) {
					Fail("关闭兑换时商店身份校验失败");
					return;
				}
				shopId = Visible("ShopExchangeCurrency")->Id;
				stage = Stage.CloseShop;
				closeSent = false;
			} else {
				stage = Stage.ReturnToRegistration;
			}
		}

		if (stage == Stage.Receipt) {
			TickReceipt(now, autoPurchaseEnabled && !entryPriority);
			return;
		}
		if (HasBlockingDialog()) {
			status = "等待其他交互窗口关闭";
			return;
		}

		switch (stage) {
			case Stage.Check:
				if (!ValidateExchangeData()) {
					Fail("本地兑换员、货币或宝箱数据未通过校验");
					return;
				}
				var points = ReadPoints();
				if (!points.HasValue) {
					Fail("无法读取金碟点余额");
					return;
				}
				if (points.Value < BatchSize * ChestCost) {
					Finish(autoQueueEnabled, $"金碟点不足 {BatchSize * ChestCost}，跳过购买并继续排本");
					return;
				}
				var inventory = ReadInventory();
				if (!inventory.HasValue) {
					Fail("背包尚未加载，无法确认宝箱容量");
					return;
				}
				if (inventory.Value.Capacity < BatchSize) {
					Fail($"背包无法容纳 {BatchSize} 个魔光键影宝箱，请先整理背包");
					return;
				}
				if (FindNpc(ExchangeNpcId) == null || FindNpc(RegistrationNpcId) == null) {
					Fail("未找到兑换员或报名登记员");
					return;
				}
				Enter(Stage.MoveToExchange, now, "前往金碟点兑换员");
				goto case Stage.MoveToExchange;
			case Stage.MoveToExchange:
				var exchangeNpc = FindNpc(ExchangeNpcId);
				if (exchangeNpc == null) {
					Fail("兑换员已离开可见范围");
					return;
				}
				if (!Near(exchangeNpc)) {
					if (!MoveNear(exchangeNpc)) {
						Fail("无法移动至兑换员附近");
						return;
					}
					Enter(Stage.OpenShop, now, "已到兑换员附近，等待位置稳定");
					settleUntil = now + 750;
					return;
				}
				Enter(Stage.OpenShop, now, "准备打开兑换商店");
				goto case Stage.OpenShop;
			case Stage.OpenShop:
				if (Visible("ShopExchangeCurrency") != null) {
					Fail("兑换交互前商店窗口已打开");
					return;
				}
				if (!InteractWithExchangeNpc()) {
					status = "等待兑换员交互就绪";
					return;
				}
				Enter(Stage.WaitShop, now, "等待兑换商店打开");
				return;
			case Stage.WaitShop:
				var shop = Visible("ShopExchangeCurrency");
				if (shop == null) return;
				if (!VerifyShopIdentity(shop)) {
					Fail("兑换商店或金碟点身份校验未通过");
					return;
				}
				shopId = shop->Id;
				var other = FindOtherCategory(shop);
				if (other == null) {
					Fail("未找到兑换商店的“其他”分类按钮");
					return;
				}
				if (!other->IsChecked) {
					if (!SelectOtherCategory(shop, other)) {
						Fail("无法选择兑换商店的“其他”分类");
						return;
					}
					Enter(Stage.WaitCategory, now, "等待“其他”分类的宝箱商品加载");
					settleUntil = now + 500;
					return;
				}
				Enter(Stage.WaitCategory, now, "核对宝箱商品");
				goto case Stage.WaitCategory;
			case Stage.WaitCategory:
				shop = Visible("ShopExchangeCurrency");
				if (shop == null || shop->Id != shopId || !VerifyShopIdentity(shop)) {
					Fail("切换分类时兑换窗口已变化");
					return;
				}
				var category = FindOtherCategory(shop);
				if (category == null || !category->IsChecked || !TryReadShopItem(shop, out var itemSlot)) return;
				shopItemSlot = itemSlot;
				Enter(Stage.Buy, now, "已核对魔光键影宝箱，准备批量兑换");
				goto case Stage.Buy;
			case Stage.Buy:
				shop = Visible("ShopExchangeCurrency");
				points = ReadPoints();
				inventory = ReadInventory();
				if (shop == null || shop->Id != shopId || !VerifyShopIdentity(shop) || !TryReadShopItem(shop, out itemSlot) || itemSlot != shopItemSlot || !points.HasValue || !inventory.HasValue) {
					Fail("兑换前无法重新核实商店、点数或背包");
					return;
				}
				if (points.Value < BatchSize * ChestCost) {
					Enter(Stage.CloseShop, now, "点数已变化，关闭商店并返回报名点");
					return;
				}
				if (inventory.Value.Capacity < BatchSize) {
					Fail("剩余背包容量不足以容纳整批宝箱");
					return;
				}
				beforePoints = points.Value;
				beforeChests = inventory.Value.Chests;
				if (!SendPurchase(shop, itemSlot, BatchSize)) {
					Fail("兑换请求未能发送");
					return;
				}
				confirmed = cancelled = false;
				Enter(Stage.Receipt, now, $"已请求兑换 {BatchSize} 个宝箱，等待确认与到账");
				return;
			case Stage.CloseShop:
				shop = Visible("ShopExchangeCurrency");
				if (shop != null) {
					if (shop->Id != shopId || !VerifyShopIdentity(shop)) {
						Fail("关闭前兑换窗口身份校验失败");
						return;
					}
					if (Visible("SelectYesno") != null || closeSent) return;
					if (!shop->Close(true)) {
						Fail("未能关闭兑换商店");
						return;
					}
					closeSent = true;
					settleUntil = now + 500;
					status = "等待兑换商店关闭";
					return;
				}
				if (yieldedToEntry) {
					Finish(false, "商店已关闭，优先进本");
					return;
				}
				Enter(Stage.ReturnToRegistration, now, "返回报名登记员");
				goto case Stage.ReturnToRegistration;
			case Stage.ReturnToRegistration:
				var registrationNpc = FindNpc(RegistrationNpcId);
				if (registrationNpc == null) {
					Fail("未找到报名登记员，无法返回报名点");
					return;
				}
				if (Near(registrationNpc)) {
					Finish(autoQueueEnabled, "已回到报名点，继续排本");
					return;
				}
				if (!MoveNear(registrationNpc)) {
					Fail("无法移动至报名登记员附近");
					return;
				}
				Enter(Stage.CheckReturn, now, "已返回报名点，等待位置确认");
				settleUntil = now + 750;
				return;
			case Stage.CheckReturn:
				registrationNpc = FindNpc(RegistrationNpcId);
				if (Near(registrationNpc)) {
					Finish(autoQueueEnabled, "兑换流程完成，继续排本");
				} else {
					Fail("未能确认已回到报名登记员附近");
				}
				return;
		}
	}

	private int shopItemSlot = -1;

	private void TickReceipt(long now, bool mayConfirm) {
		var points = ReadPoints();
		var inventory = ReadInventory();
		var confirmation = Visible("SelectYesno");
		if (points == beforePoints - BatchSize * ChestCost && inventory?.Chests == beforeChests + BatchSize) {
			Enter(Stage.CloseShop, now, "已同时确认点数扣除和宝箱到账，关闭商店");
			return;
		}
		if (cancelled && confirmation == null && points == beforePoints && inventory?.Chests == beforeChests) {
			Enter(Stage.CloseShop, now, "已取消尚未确认的兑换");
			return;
		}
		if (confirmation != null && !confirmed && !cancelled) {
			if (HasBlockingDialog()) {
				Fail("兑换确认期间出现其他交互窗口，已保留确认框供核对");
				return;
			}
			if (!VerifyConfirmation(confirmation)) {
				Fail("兑换确认内容与宝箱或金额不符，已保留窗口供手动核对");
				return;
			}
			var accept = mayConfirm && Plugin.autoPurchase;
			if (!confirmation->FireCallbackInt(accept ? 0 : 1)) {
				Fail("无法处理兑换确认窗口");
				return;
			}
			confirmed = accept;
			cancelled = !accept;
			settleUntil = now + (accept ? 0 : 1500);
			status = accept ? "已确认兑换，等待点数扣除和宝箱到账" : "已取消兑换确认，等待状态稳定";
			return;
		}
		status = confirmed ? "等待点数扣除和宝箱到账" : "等待兑换确认窗口";
	}

	private bool VerifyConfirmation(AtkUnitBase* addon) {
		if (addon == null || ReadNumber(addon, 14) != ChestItemId || ReadNumber(addon, 17) != BatchSize) return false;
		var text = ReadText(addon, 0) + "\n" + ReadText(addon, 15);
		if (!text.Contains("魔光键影宝箱", StringComparison.Ordinal) || !text.Contains("金碟点", StringComparison.Ordinal)) return false;
		var digits = new StringBuilder();
		foreach (var c in text + " ") {
			if (c is >= '0' and <= '9') {
				digits.Append(c);
			} else {
				if (int.TryParse(digits.ToString(), out var amount) && amount == BatchSize * ChestCost) return true;
				digits.Clear();
			}
		}
		return false;
	}

	private void Enter(Stage next, long now, string message) {
		stage = next;
		deadline = now + 15000;
		status = message;
		if (next == Stage.CloseShop) closeSent = false;
	}

	private void Finish(bool requestRace, string message) {
		stage = Stage.Idle;
		deadline = 0;
		settleUntil = 0;
		shopId = 0;
		shopItemSlot = -1;
		status = message;
		if (requestRace && !HasEntryPriority()) Plugin.RequestRace();
	}

	private void Fail(string reason) {
		stage = Stage.Failed;
		status = "自动购买已暂停：" + reason;
		Plugin.log.Warning("[LatihasKTG] {Reason}", reason);
	}

	private bool CanAct() => !Plugin.Condition[ConditionFlag.InCombat]
	                         && !Plugin.Condition[ConditionFlag.WatchingCutscene];

	private static bool ValidateExchangeData() {
		try {
			var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
			var shopSheet = Plugin.DataManager.GetExcelSheet<SpecialShop>();
			var npcSheet = Plugin.DataManager.GetExcelSheet<ENpcBase>();
			Item currency;
			Item chest;
			SpecialShop shop;
			ENpcBase npc;
			if (itemSheet == null || shopSheet == null || npcSheet == null
			    || !itemSheet.TryGetRow(CurrencyItemId, out currency) || currency.StackSize != 10000
			    || !itemSheet.TryGetRow(ChestItemId, out chest) || chest.Name.ToString() != "魔光键影宝箱" || chest.StackSize != BagStackSize
			    || !shopSheet.TryGetRow(1771021u, out shop) || shop.UseCurrencyType != 4
			    || !npcSheet.TryGetRow(ExchangeNpcId, out npc)
			    || !npc.ENpcData.Any(row => row.RowId == 1771021)) return false;

			var goods = shop.Item[19];
			var reward = goods.ReceiveItems[0];
			var extraReward = goods.ReceiveItems[1];
			var cost = goods.ItemCosts[0];
			var extraCost1 = goods.ItemCosts[1];
			var extraCost2 = goods.ItemCosts[2];
			return reward.Item.RowId == ChestItemId && reward.ReceiveCount == 1 && !reward.ReceiveHq
			       && extraReward.Item.RowId == 0 && cost.ItemCost.RowId == CurrencyItemId
			       && cost.CurrencyCost == ChestCost && cost.CostType == 0
			       && extraCost1.CurrencyCost == 0 && extraCost2.CurrencyCost == 0;
		} catch {
			return false;
		}
	}

	private bool HasEntryPriority() => Plugin.Condition[ConditionFlag.WaitingForDutyFinder]
	                                   || Plugin.Condition[ConditionFlag.InDutyQueue]
	                                   || Visible("ContentsFinderConfirm") != null
	                                   || Visible("ContentsFinderReady") != null
	                                   || Visible("KTGEnterDialogue") != null;

	private bool HasBlockingDialog() => BlockingAddons.Any(name => Visible(name) != null);

	private static AtkUnitBase* Visible(string name) {
		var address = (AtkUnitBase*)Plugin.gameGui.GetAddonByName(name).Address;
		return address != null && address->IsReady && address->IsVisible ? address : null;
	}

	private static IGameObject? FindNpc(uint baseId) => Plugin.ObjectTable.FirstOrDefault(obj =>
		(int)obj.ObjectKind == 3 && obj.BaseId == baseId && obj.IsTargetable && obj.Address != IntPtr.Zero && Finite(obj.Position)
		&& Plugin.ObjectTable.LocalPlayer != null && Vector3.Distance(Plugin.ObjectTable.LocalPlayer.Position, obj.Position) < 80f);

	private static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

	private static bool Near(IGameObject? npc) => npc != null && Plugin.ObjectTable.LocalPlayer is { } player && Vector3.Distance(player.Position, npc.Position) <= 3.5f;

	private static bool MoveNear(IGameObject npc) {
		var player = Plugin.ObjectTable.LocalPlayer;
		if (player == null || !Finite(player.Position) || !Finite(npc.Position)) return false;
		var direction = new Vector3(player.Position.X - npc.Position.X, 0, player.Position.Z - npc.Position.Z);
		direction = direction.LengthSquared() > 0.01f ? Vector3.Normalize(direction) : Vector3.UnitZ;
		var destination = npc.Position + direction * 2.5f;
		var actor = (GameObject*)player.Address;
		actor->SetPosition(destination.X, destination.Y, destination.Z);
		actor->PositionModified();
		return true;
	}

	private static bool InteractWithExchangeNpc() {
		var npc = FindNpc(ExchangeNpcId);
		if (npc == null || !Near(npc) || Visible("ShopExchangeCurrency") != null || Visible("SelectYesno") != null || TargetSystem.Instance() == null) return false;
		Plugin.TargetManager.Target = npc;
		var target = (GameObject*)npc.Address;
		TargetSystem.Instance()->SetHardTarget(target);
		TargetSystem.Instance()->InteractWithObject(target);
		return true;
	}

	private static int? ReadPoints() {
		if (!Plugin.clientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null) return null;
		var currency = CurrencyManager.Instance();
		if (currency == null || currency->GetItemMaxCount(CurrencyItemId) != 10000) return null;
		var count = currency->GetItemCount(CurrencyItemId);
		return count <= 10000 ? (int)count : null;
	}

	private static (int Chests, int Capacity)? ReadInventory() {
		var manager = InventoryManager.Instance();
		if (manager == null) return null;
		var chests = 0;
		var capacity = 0;
		foreach (var bag in Bags) {
			var container = manager->GetInventoryContainer(bag);
			if (container == null || !container->IsLoaded || container->Size is <= 0 or > 35) return null;
			for (var i = 0; i < container->Size; i++) {
				var slot = container->GetInventorySlot(i);
				if (slot == null || slot->IsSymbolic) return null;
				if (slot->ItemId == 0) capacity += BagStackSize;
				else if (slot->ItemId == ChestItemId) {
					if (slot->Quantity > BagStackSize) return null;
					chests += slot->Quantity;
					capacity += BagStackSize - slot->Quantity;
				}
			}
		}
		return (chests, capacity);
	}

	private static bool VerifyShopIdentity(AtkUnitBase* addon) {
		var npc = FindNpc(ExchangeNpcId);
		var target = Plugin.TargetManager.Target;
		var points = ReadPoints();
		return addon != null && Near(npc) && target != null && target.BaseId == ExchangeNpcId && points.HasValue && ReadNumber(addon, 86) == points;
	}

	private static bool TryReadShopItem(AtkUnitBase* addon, out int itemSlot) {
		itemSlot = -1;
		if (!VerifyShopIdentity(addon)) return false;
		var other = FindOtherCategory(addon);
		if (other == null || !other->IsChecked || addon->AtkValues == null) return false;
		var matchCount = 0;
		for (var i = 0; i < 122; i++) {
			if (ReadNumber(addon, 1066 + i) != ChestItemId) continue;
			if (ReadText(addon, 90 + i) != "魔光键影宝箱"
			    || ReadNumber(addon, 456 + i) != ChestCost
			    || ReadNumber(addon, 822 + i) != 1
			    || ReadNumber(addon, 1188 + i) != BagStackSize
			    || ReadNumber(addon, 1310 + i) != i) return false;
			itemSlot = i;
			matchCount++;
		}
		return matchCount == 1;
	}

	private static AtkComponentRadioButton* FindOtherCategory(AtkUnitBase* addon) {
		if (addon == null || addon->UldManager.NodeList == null) return null;
		AtkComponentRadioButton* result = null;
		for (var i = 0; i < Math.Min(256, (int)addon->UldManager.NodeListCount); i++) {
			var node = addon->UldManager.NodeList[i];
			if (node == null || (int)node->Type < 1000 || !node->IsVisible()) continue;
			var component = ((AtkComponentNode*)node)->Component;
			if (component == null || (int)component->GetComponentType() != 4) continue;
			var button = (AtkComponentRadioButton*)component;
			if (button->ButtonTextNode == null || !button->IsEnabled) continue;
			var label = ReadNativeText(button->ButtonTextNode->NodeText.StringPtr).Trim();
			if (label != "其他") continue;
			if (result != null) return null;
			result = button;
		}
		return result;
	}

	private static bool SelectOtherCategory(AtkUnitBase* addon, AtkComponentRadioButton* button) {
		if (addon == null || button == null || button->OwnerNode == null) return false;
		var node = &button->OwnerNode->AtkResNode;
		if (!node->IsEventRegistered((AtkEventType)25)) return false;
		for (var i = 0; i < Math.Min(256, (int)addon->UldManager.NodeListCount); i++) {
			var current = addon->UldManager.NodeList[i];
			if (current == null || (int)current->Type < 1000) continue;
			var component = ((AtkComponentNode*)current)->Component;
			if (component == null || component == (AtkComponentBase*)button || (int)component->GetComponentType() != 4) continue;
			var radio = (AtkComponentRadioButton*)component;
			if (radio->ButtonTextNode == null) continue;
			var label = ReadNativeText(radio->ButtonTextNode->NodeText.StringPtr).Trim();
			if (label is "武器" or "防具" or "饰品") radio->SetChecked(false);
		}
		button->SetChecked(true);
		var evt = stackalloc AtkEventDispatcher.Event[1];
		evt->State.EventType = (AtkEventType)25;
		return node->DispatchEvent(evt);
	}

	private static bool SendPurchase(AtkUnitBase* addon, int itemSlot, int quantity) {
		if (addon == null || itemSlot is < 0 or >= 122 || quantity != BatchSize) return false;
		var values = stackalloc AtkValue[4];
		new Span<AtkValue>(values, 4).Clear();
		for (var i = 0; i < 3; i++) values[i].Type = AtkValueType.Int;
		values[0].Int = 0;
		values[1].Int = itemSlot;
		values[2].Int = quantity;
		return addon->FireCallback(4, values, true);
	}

	private static uint? ReadNumber(AtkUnitBase* addon, int index) {
		if (addon == null || addon->AtkValues == null || index < 0 || index >= addon->AtkValuesCount) return null;
		var value = (AtkValue*)((byte*)addon->AtkValues + (nint)index * Unsafe.SizeOf<AtkValue>());
		return value->Type switch {
			AtkValueType.Int when value->Int >= 0 => (uint)value->Int,
			AtkValueType.UInt => value->UInt,
			_ => null
		};
	}

	private static string ReadText(AtkUnitBase* addon, int index) {
		if (addon == null || addon->AtkValues == null || index < 0 || index >= addon->AtkValuesCount) return "";
		var value = (AtkValue*)((byte*)addon->AtkValues + (nint)index * Unsafe.SizeOf<AtkValue>());
		if (value->Type is not (AtkValueType.String or AtkValueType.WideString or AtkValueType.ManagedString)) return "";
		var pointer = (byte*)value->String;
		if (pointer == null) return "";
		var length = 0;
		while (length < 4096 && pointer[length] != 0) length++;
		if (length == 4096) return "";
		try {
			return SeString.Parse(new ReadOnlySpan<byte>(pointer, length).ToArray()).TextValue;
		} catch {
			return "";
		}
	}

	private static string ReadNativeText(byte* pointer) {
		if (pointer == null) return "";
		var length = 0;
		while (length < 4096 && pointer[length] != 0) length++;
		return length < 4096 ? Encoding.UTF8.GetString(new ReadOnlySpan<byte>(pointer, length)) : "";
	}

	public void Dispose() {
		stage = Stage.Idle;
		status = "自动购买已停止";
	}
}