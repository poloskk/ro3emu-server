using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Auto battle gives up on a monster it cannot fight: when the enemy it attacks has not lost any life
    /// <see cref="GiveUpSeconds"/> after it was picked, it is skipped for <see cref="SkipSeconds"/> and the next one is
    /// taken. A player's choice, not the original game's.
    /// <para>
    /// The client's own give-up (LC_AutoAttack_AttackSubState.OnUpdate: back to Seek after AttackToSeekTime, which is
    /// publicparamconfig.AutoBattleAutoUseTimeLimit, 3 s, without a skill pick) restarts its clock whenever TryUpdate
    /// sends a cast or chases (_runSkillCircle -> _tryChaseToTarget), so a character chasing a monster it never reaches, or
    /// casting at one the server refuses, stays on it. Three module functions are wrapped:
    /// </para>
    /// <list type="bullet">
    /// <item>lc_autoattack_attacksubstate.OnUpdate(self, ctx): after the original, follows self.m_kTarget (an enemy:
    /// LC_CampRelationUtils.IsEnemyByUnit, as SetTreeTarget) and its GetHpPercent() (0-1); a target that has lost none in
    /// time is skipped and the attack id it held is cleared (lc_autoattackhelper.ClearNotValidAttackId);</item>
    /// <item>lc_autoattackhelper.IsValidBySkillId(owner, unit, skillId), which PickBestTarget asks of every candidate, and
    /// CheckPreTargetValidSimple(owner, unit, skillId), which GetCurTarget asks of the player's selection (false drops the
    /// selection: its "Unpick" path), are false for a skipped monster.</item>
    /// </list>
    /// </summary>
    public static class AutoTargetSwitch
    {
        private const float GiveUpSeconds = 3f;
        private const float SkipSeconds = 20f;
        /// <summary>A target not seen in the attack state for this long (seeking, auto battle off) starts afresh.</summary>
        private const float AwaySeconds = 1f;
        /// <summary>Less than this lost is no hit (GetHpPercent is a float share).</summary>
        private const double HitEpsilon = 1e-6;

        /// <summary>The attack substate's OnUpdate(self, ctx) -> the next on-hook state.</summary>
        public delegate object Update(Obf_R.Obf_ff self, object ctx);
        /// <summary>A target check called as Helper.Check(owner, unit, skillId) -> true when the unit may be attacked.</summary>
        public delegate bool Check(object owner, params object[] rest);

        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly Update UpdateWrapper = OnUpdate;
        private static readonly Check ValidBySkillWrapper = IsValidBySkillId, PreTargetWrapper = CheckPreTargetValidSimple;
        private static Obf_R.Obf_Ef _update, _validBySkill, _preTarget, _clearAttackId, _isEnemy;
        private static readonly Dictionary<string, float> Skipped = new Dictionary<string, float>();
        private static string _target;
        private static float _since, _seen;
        private static double _life;
        private static bool _hit, _reported;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff attack, Obf_R.Obf_ff helper)
        {
            _update = _validBySkill = _preTarget = null;
            Skipped.Clear();
            _target = null;
            try
            {
                _clearAttackId = helper?.Obf_RuA<Obf_R.Obf_Ef>("ClearNotValidAttackId");
                _isEnemy = global.Obf_LuA<Obf_R.Obf_Ef>("LC_CampRelationUtils.IsEnemyByUnit");
                var update = attack?.Obf_RuA<Obf_R.Obf_Ef>("OnUpdate");
                var validBySkill = helper?.Obf_RuA<Obf_R.Obf_Ef>("IsValidBySkillId");
                var preTarget = helper?.Obf_RuA<Obf_R.Obf_Ef>("CheckPreTargetValidSimple");
                if (update == null || validBySkill == null || preTarget == null || _clearAttackId == null || _isEnemy == null)
                {
                    Debug.LogWarning("[Ro3Emu.AutoTargetSwitch] auto-attack functions not found; auto battle keeps its own targeting");
                    return;
                }
                (_update, _validBySkill, _preTarget) = (update, validBySkill, preTarget);
                attack.Obf_luA<string, Update>("OnUpdate", UpdateWrapper);
                helper.Obf_luA<string, Check>("IsValidBySkillId", ValidBySkillWrapper);
                helper.Obf_luA<string, Check>("CheckPreTargetValidSimple", PreTargetWrapper);
                Debug.Log("[Ro3Emu.AutoTargetSwitch] auto battle skips a target it has not hit in " + GiveUpSeconds + " s");
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.AutoTargetSwitch] " + e.GetType().Name + ": " + e.Message); }
        }

        private static object OnUpdate(Obf_R.Obf_ff self, object ctx)
        {
            var result = _update?.Obf_HuA(self, ctx);
            try { Follow(self?.Obf_RuA<object>("m_kTarget") as Obf_R.Obf_ff); }
            catch (Exception e) { Once(e); }
            return result != null && result.Length > 0 ? result[0] : null;
        }

        /// <summary>Starts the clock on a new enemy target, stops it on a hit, and skips the target when time runs out.</summary>
        private static void Follow(Obf_R.Obf_ff target)
        {
            // No enemy this frame (between skills, a friend's buff) keeps the clock; one away for a while restarts it.
            string id = target == null ? null : Convert.ToString(target.Obf_RuA<object>("m_kUUID"));
            if (string.IsNullOrEmpty(id) || !Truthy(_isEnemy.Obf_HuA(target))) return;
            double life = Life(target);
            float now = Time.realtimeSinceStartup;
            bool away = now - _seen > AwaySeconds;
            _seen = now;
            if (id != _target || away)
            {
                (_target, _since, _life, _hit) = (id, now, life, false);
                return;
            }
            if (_hit) return;
            if (life < _life - HitEpsilon) { _hit = true; return; }
            if (now - _since < GiveUpSeconds) return;
            Skipped[id] = now + SkipSeconds;
            _target = null;
            _clearAttackId.Obf_HuA();
            Debug.Log("[Ro3Emu.AutoTargetSwitch] no hit on " + id + " in " + GiveUpSeconds + " s: next target");
        }

        private static bool IsValidBySkillId(object owner, params object[] rest) =>
            !IsSkipped(rest) && Truthy(_validBySkill?.Obf_HuA(Args(owner, rest)));

        private static bool CheckPreTargetValidSimple(object owner, params object[] rest) =>
            !IsSkipped(rest) && Truthy(_preTarget?.Obf_HuA(Args(owner, rest)));

        /// <summary>The unit argument (after the owner) is a monster skipped a moment ago.</summary>
        private static bool IsSkipped(object[] rest)
        {
            if (Skipped.Count == 0 || rest == null || rest.Length == 0 || !(rest[0] is Obf_R.Obf_ff unit)) return false;
            try
            {
                string id = Convert.ToString(unit.Obf_RuA<object>("m_kUUID"));
                if (string.IsNullOrEmpty(id) || !Skipped.TryGetValue(id, out float until)) return false;
                if (Time.realtimeSinceStartup < until) return true;
                Skipped.Remove(id);
            }
            catch (Exception e) { Once(e); }
            return false;
        }

        private static double Life(Obf_R.Obf_ff unit)
        {
            var result = unit.Obf_RuA<Obf_R.Obf_Ef>("GetHpPercent")?.Obf_HuA(unit);
            return result != null && result.Length > 0 && result[0] is IConvertible value ? value.ToDouble(null) : 1;
        }

        private static object[] Args(object owner, object[] rest)
        {
            var args = new object[1 + (rest?.Length ?? 0)];
            args[0] = owner;
            rest?.CopyTo(args, 1);
            return args;
        }

        /// <summary>Lua truth: anything but nil and false.</summary>
        private static bool Truthy(object[] result) => result != null && result.Length > 0 && result[0] != null && !(result[0] is bool b && !b);

        private static void Once(Exception e)
        {
            if (_reported) return;
            _reported = true;
            Debug.LogWarning("[Ro3Emu.AutoTargetSwitch] " + e.GetType().Name + ": " + e.Message);
        }
    }
}
