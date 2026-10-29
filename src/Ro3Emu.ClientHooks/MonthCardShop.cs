using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The monthly pass buttons buy in game again. This publisher build sends both of them to its web shop
    /// (lc_clientlinks.OpenRecharge, PublicParamConfig.RechargeExternalUrl): "Quick Buy"
    /// (UI_Sys_ThreeDays_MonthCardWidgetCtrl._onClickBuyAll) and a card's own buy (UI_Sys_ThreeDays_MonthCardItemWidgetCtrl
    /// ._onClickBuy). Both become the client's own order, LC_MonthCardHelper.Purchase(product) → 2001 CREATE_ORDER (sent
    /// by PaySdk), which
    /// the server answers (NewActivityService.OrderMonthCardAsync): Quick Buy orders
    /// PublicParamConfig.MonthCardAllBuyProductConfigID, a card its LC_MonthCardHelper.GetProduct(m_iCardId). A widget at
    /// its stockpiling limit (m_bIsUpperLimit) buys nothing, as its greyed button shows.
    /// </summary>
    public static class MonthCardShop
    {
        /// <summary>A button handler: self, then whatever the click passes (xLua matches a delegate's argument count
        /// exactly unless it ends in params).</summary>
        public delegate void Click(Obf_R.Obf_ff self, params object[] rest);

        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly Click BuyAllWrapper = BuyAll, BuyOneWrapper = BuyOne;
        private static Obf_R.Obf_ff _helper, _param;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            _helper = LuaPatches.Module(manager, global, "lc_monthcardhelper");
            _param = LuaPatches.Module(manager, global, "publicparamconfig");
            var panel = LuaPatches.Module(manager, global, "ui_sys_threedays_monthcardwidgetctrl");
            var item = LuaPatches.Module(manager, global, "ui_sys_threedays_monthcarditemwidgetctrl");
            if (_helper == null || panel == null || item == null)
            {
                Debug.LogWarning("[Ro3Emu.MonthCardShop] month card modules not found; the buttons keep opening the web shop");
                return;
            }
            // BaseClass's __newindex writes the shared vtbl; the widgets bind their buttons when they open, after this.
            panel.Obf_luA<string, Click>("_onClickBuyAll", BuyAllWrapper);
            item.Obf_luA<string, Click>("_onClickBuy", BuyOneWrapper);
            Debug.Log("[Ro3Emu.MonthCardShop] monthly pass buttons buy in game");
        }

        private static void BuyAll(Obf_R.Obf_ff self, params object[] rest)
        {
            if (Full(self)) return;
            Purchase(LuaPatches.ToLong(_param?.Obf_RuA<object>("MonthCardAllBuyProductConfigID")));
        }

        private static void BuyOne(Obf_R.Obf_ff self, params object[] rest)
        {
            if (Full(self) || _helper == null) return;
            var product = _helper.Obf_RuA<Obf_R.Obf_Ef>("GetProduct")?.Obf_HuA(self.Obf_RuA<object>("m_iCardId"));
            Purchase(product != null && product.Length > 0 ? LuaPatches.ToLong(product[0]) : long.MinValue);
        }

        private static bool Full(Obf_R.Obf_ff self) => self.Obf_RuA<object>("m_bIsUpperLimit") is bool full && full;

        private static void Purchase(long product)
        {
            try
            {
                if (product > 0) _helper?.Obf_RuA<Obf_R.Obf_Ef>("Purchase")?.Obf_HuA(product);
                else Debug.LogWarning("[Ro3Emu.MonthCardShop] no product for this button");
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.MonthCardShop] " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
