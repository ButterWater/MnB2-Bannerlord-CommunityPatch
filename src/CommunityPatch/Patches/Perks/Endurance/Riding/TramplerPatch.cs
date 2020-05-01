using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using HarmonyLib;
using TaleWorlds.MountAndBlade;
using static CommunityPatch.HarmonyHelpers;

namespace CommunityPatch.Patches.Perks.Endurance.Riding {

  public sealed class TramplerPatch : PatchBase<TramplerPatch> {

    public override bool Applied { get; protected set; }

    private static readonly MethodInfo TargetMethodInfo = typeof(Mission).GetMethod("GetAttackCollisionResults", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    private static readonly MethodInfo OverloadedTargetMethodInfo = typeof(Mission).GetMethod("GetAttackCollisionResults", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

    private static readonly MethodInfo TranspilerPatchMethodInfo = typeof(TramplerPatch).GetMethod(nameof(Transpiler), BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);

    private static readonly MethodInfo PostfixPatchMethodInfo = typeof(TramplerPatch).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);

    public override IEnumerable<MethodBase> GetMethodsChecked() {
      yield return TargetMethodInfo;
      yield return OverloadedTargetMethodInfo;
    }

    private static readonly byte[][] Hashes = {
      new byte[] {
        // e1.2.1.227410
        0xC0, 0xBC, 0x9E, 0xDE, 0xAF, 0xAB, 0xFE, 0xE0,
        0x21, 0x89, 0xAE, 0x21, 0xB5, 0xA4, 0x97, 0x58,
        0xE8, 0x48, 0xE4, 0x75, 0xF0, 0x58, 0xE8, 0xC5,
        0x86, 0x06, 0xC1, 0xE8, 0x6D, 0x42, 0x9A, 0xAA
      },
      new byte[] {
        // e1.2.1.227410
        0x3C, 0x34, 0x56, 0x68, 0xCE, 0xD5, 0x86, 0x1B,
        0x38, 0x81, 0xCA, 0x3C, 0x8B, 0xB3, 0xF9, 0xE9,
        0xDF, 0x22, 0x86, 0xB0, 0xCA, 0xBF, 0xCB, 0xAF,
        0xAA, 0xCF, 0x99, 0xCD, 0x4A, 0x4D, 0xB6, 0x2C
      },
    };

    public override void Reset() {
    }

    public override bool? IsApplicable(Game game)
      => TargetMethodInfo != null
        && OverloadedTargetMethodInfo != null
        && !AlreadyPatchedByOthers(Harmony.GetPatchInfo(TargetMethodInfo))
        && TargetMethodInfo.MakeCilSignatureSha256().MatchesAnySha256(Hashes[0])
        && !AlreadyPatchedByOthers(Harmony.GetPatchInfo(OverloadedTargetMethodInfo))
        && OverloadedTargetMethodInfo.MakeCilSignatureSha256().MatchesAnySha256(Hashes[1]);

    public override void Apply(Game game) {
      if (Applied) return;

      CommunityPatchSubModule.Harmony.Patch(TargetMethodInfo,
        transpiler: new HarmonyMethod(TranspilerPatchMethodInfo));

      CommunityPatchSubModule.Harmony.Patch(OverloadedTargetMethodInfo,
        postfix: new HarmonyMethod(PostfixPatchMethodInfo));
      Applied = true;
    }

    private static readonly MethodInfo GetCharacterMethod = typeof(Agent)
      .GetMethod("get_Character", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    private static bool IsAttackerAgentAssignedAndAgentRiderNotNull(List<CodeInstruction> instructions, int start)
      => start + 4 < instructions.Count
        && instructions[start].opcode == OpCodes.Ldarg_1
        && instructions[start + 1].opcode == OpCodes.Callvirt
        && instructions[start + 1].operand is MethodInfo mi && mi == GetCharacterMethod
        && instructions[start + 2].opcode == OpCodes.Stloc_S
        && instructions[start + 3].opcode == OpCodes.Ldloc_S
        && instructions[start + 4].opcode == OpCodes.Brfalse_S;

    private static BasicCharacterObject UpdateCorrectCharacterForHorseChargeDamage(Agent agent, ref AttackCollisionData acd, BasicCharacterObject character)
      => acd.IsHorseCharge && agent.RiderAgent != null ? agent.RiderAgent.Character : character;

    private static readonly MethodInfo CorrectCharacterForHorseChargeDamageMethod = typeof(TramplerPatch)
      .GetMethod("UpdateCorrectCharacterForHorseChargeDamage", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);

    private static readonly List<LocalVariableInfo> CharacterLocalVariableInfos = TargetMethodInfo
      .GetMethodBody()!.LocalVariables.Where(var => var.LocalType == typeof(BasicCharacterObject)).ToList();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
      if (CharacterLocalVariableInfos.Count != 2) {
        CommunityPatchSubModule.Error($"{nameof(TramplerPatch)}: Expected two BasicCharacterObject  local variable instances in the original method."
          + $" The original code has been changed requiring an update of this patch." + Environment.NewLine);
        return instructions;
      }

      var codes = instructions.ToList();
      for (var index = 0; index < codes.Count - 5; index++) {
        if (IsAttackerAgentAssignedAndAgentRiderNotNull(codes, index)) {
          var attackerCharacterLocalVariableIndex = CharacterLocalVariableInfos[1].LocalIndex;
          codes.InsertRange(index + 5, new List<CodeInstruction> {
            new CodeInstruction(OpCodes.Ldarg_1), // Load attackerAgent argument
            new CodeInstruction(OpCodes.Ldarg_S, 5), // Load attackCollisionData argument
            new CodeInstruction(OpCodes.Ldloc_S, attackerCharacterLocalVariableIndex), // Load attackerCharacter local variable
            new CodeInstruction(OpCodes.Call, CorrectCharacterForHorseChargeDamageMethod), // Update attackerCharacter if the collision is a horse charge
            new CodeInstruction(OpCodes.Stloc_S, attackerCharacterLocalVariableIndex) // Store value in attackerCharacter
          });
          return codes.AsEnumerable();
        }
      }

      CommunityPatchSubModule.Error($"{nameof(TramplerPatch)}: Could not find the starting point to add new instructions in {TargetMethodInfo}." + Environment.NewLine);
      return codes.AsEnumerable();
    }

    private static bool HeroHasPerk(BasicCharacterObject character, PerkObject perk)
      => (character as CharacterObject)?.GetPerkValue(perk) ?? false;

    private static int TramplerDamageModifier(int baseDamage) {
      var tramplerDamage = baseDamage * (1f + DefaultPerks.Riding.Trampler.PrimaryBonus);
      return (int) Math.Round(tramplerDamage, MidpointRounding.AwayFromZero);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(BasicCharacterObject attackerAgentCharacter, ref AttackCollisionData attackCollisionData, ref CombatLogData combatLog) {
      if (!(attackCollisionData.IsHorseCharge && HeroHasPerk(attackerAgentCharacter, DefaultPerks.Riding.Trampler))) {
        return;
      }

      combatLog.InflictedDamage = TramplerDamageModifier(combatLog.InflictedDamage);
      attackCollisionData.InflictedDamage = TramplerDamageModifier(attackCollisionData.InflictedDamage);
    }

  }

}