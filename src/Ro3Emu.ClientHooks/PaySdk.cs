using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Lua_SDK_PayMgr without a store SDK. Lua_SDK_Factory.GetPayMgr() runs a new manager's Init before it keeps it, and
    /// Init asks MetaSDKUtil.ReqSkuInfo for the store's prices. With no store SDK ("sdk proxy is not init") that answers
    /// at once through GameLogic.OnSkuInfoResult, which calls GetPayMgr() again and makes another manager, until Lua's C
    /// stack overflows: the first buy froze the client. Here Init does nothing, and ReqPurchaseRequest sends its 2001
    /// CREATE_ORDER without asking MetaSDKUtil for the account's country code. LC_MonthCardHelper.Purchase (the monthly
    /// pass buttons, MonthCardShop) and LC_BattlePassHelper.RequestBuyBattlePass both order through it.
    /// </summary>
    public static class PaySdk
    {
        /// <summary>A manager method: self, then whatever the caller passes (xLua matches a delegate's argument count
        /// exactly unless it ends in params).</summary>
        public delegate void Method(Obf_R.Obf_ff self, params object[] rest);

        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly Method InitWrapper = Init, RequestWrapper = Request;
        private static Obf_R.Obf_ff _global;

        /// <summary>From LuaPatches.Tick, once per LuaEnv, after PassChallenges.Install (its Row builds the order).</summary>
        internal static void Install(LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            _global = global;
            var pay = LuaPatches.Module(manager, global, "lua_sdk_paymgr");
            if (pay == null)
            {
                Debug.LogWarning("[Ro3Emu.PaySdk] lua_sdk_paymgr not found; buying stays broken");
                return;
            }
            // BaseClass's __newindex writes the shared vtbl; the factory makes its manager on the first buy, after this.
            pay.Obf_luA<string, Method>("Init", InitWrapper);
            pay.Obf_luA<string, Method>("ReqPurchaseRequest", RequestWrapper);
            Debug.Log("[Ro3Emu.PaySdk] pay manager orders without the store SDK");
        }

        private static void Init(Obf_R.Obf_ff self, params object[] rest) { }

        /// <summary>The original's order table (Lua_SDK_PayMgr.ReqPurchaseRequest): chargeType, then the ProductConfig or
        /// RechargeConfig row. The PC build's payType is empty, as is its regionCode here.</summary>
        private static void Request(Obf_R.Obf_ff self, params object[] rest)
        {
            try
            {
                if (rest.Length < 2 || !(rest[1] is Obf_R.Obf_ff product))
                {
                    Debug.LogWarning("[Ro3Emu.PaySdk] an order without its product row");
                    return;
                }
                var order = PassChallenges.Row(
                    "amount", product.Obf_RuA<object>("_iPrice"),
                    "chargeType", rest[0],
                    "chargeId", product.Obf_RuA<object>("_iID"),
                    "packageName", Application.identifier,
                    "payType", "",
                    "tradeType", "",
                    "subject", product.Obf_RuA<object>("_kProductName"),
                    "regionCode", "");
                _global.Obf_LuA<Obf_R.Obf_Ef>("NW_Helper.SendMessage")
                    .Obf_HuA(_global.Obf_LuA<object>("ProtobufNameMap.CREATE_ORDER"), order);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.PaySdk] " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
