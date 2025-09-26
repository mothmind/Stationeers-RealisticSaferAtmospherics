using HarmonyLib;
using JetBrains.Annotations;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Atmospherics;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Reflection;
using System;
using Assets.Scripts.Util;
using Objects.Pipes;

namespace RealisticSaferAtmospherics
{
  [HarmonyPatch(typeof(PressureRegulator))]
  public static class PressureRegulatorPatch
  {
    [HarmonyPatch("OnAtmosphericTick")]
    [HarmonyPrefix]
    [UsedImplicitly]
    static private void PressureRegulatorAlwaysPowered(PressureRegulator __instance)
    {
      __instance.PoweredValue = 1; // Always powered
      __instance.UsedPower = 5.0f; // Plugging in allows data connection at minor cost of power
      var pressurePerTickProperty = AccessTools.Field(typeof(PressureRegulator), "pressurePerTick");
      if (pressurePerTickProperty != null)
      {
        pressurePerTickProperty.SetValue(__instance, 0.0f); // Pressure Regulator only goes high to low
      }
    }
  }

  [HarmonyPatch(typeof(AtmosphereHelper))]
  public static class AtmosphereHelperPatch
  {
    [HarmonyPatch("MaxMolesPerTick")]
    [HarmonyTranspiler]
    [UsedImplicitly]
    static IEnumerable<CodeInstruction> RegulatorBuffTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      foreach (var instruction in instructions)
      {
        if (instruction.opcode == OpCodes.Ldc_R8 && (double)instruction.operand == 4.0)
        {
          instruction.operand = 1.0; // Makes regulators equalize pressure roughly at the speed of valves
        }
      }
      return instructions;
    }
  }

  public static class PumpModifier
  {
    public static double getFlowRateModifier(double inputPressure, double outputPressure, double maxDifferential)
    {
      double linearFalloff = 1 - (outputPressure - inputPressure) / maxDifferential;
      double quadraticFalloff = Math.Pow(linearFalloff, 1);
      return Math.Max(quadraticFalloff, 0.0);
    }

    // Copied from AtmosphereHelper, flow rate tapers off as we approach max pressure difference
    public static void MoveVolumeCapped(Atmosphere inputAtmos, Atmosphere outputAtmos, VolumeLitres volume, AtmosphereHelper.MatterState matterStateToMove, double maxPressureDifference = 5000.0)
    {
      double num = RocketMath.Clamp(volume / inputAtmos.GetVolume(matterStateToMove), VolumeLitres.Zero, inputAtmos.GetVolume(matterStateToMove)).ToDouble();
      // Added modifier to num based on pressure difference
      num *= getFlowRateModifier(inputAtmos.PressureGassesAndLiquids.ToDouble(), outputAtmos.PressureGassesAndLiquids.ToDouble(), maxPressureDifference);
      if (num <= 0.0)
      {
        return;
      }
      GasMixture gasMixture = inputAtmos.Remove(inputAtmos.GasMixture.GetTotalMoles(matterStateToMove) * num, matterStateToMove);
      outputAtmos.Add(gasMixture);
    }
  }

  [HarmonyPatch(typeof(ActiveVent))]
  public static class ActiveVentPatch
  {
    [HarmonyPatch("PumpGasToPipe")]
    [HarmonyTranspiler]
    [UsedImplicitly]
    static IEnumerable<CodeInstruction> ActiveVentTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return instructions;
    }
  }


  [HarmonyPatch(typeof(VolumePump))]
  public static class VolumePumpPatch
  {
    [HarmonyPatch("MoveAtmosphere")]
    [HarmonyTranspiler]
    [UsedImplicitly]
    static IEnumerable<CodeInstruction> VolumePumpTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      const float turboMinOperatingSoundPitch = 0.75f; // Using sound pitch to differentiate pump types
      const double normalVolumePumpMaxDiff = 10000.0; // 10 MPa cap for volume pumps

      const double turboVolumePumpMaxDiff = 40000.0; // 40 MPa cap for turbo volume pumps

      var moveVolumeCapped = AccessTools.Method(typeof(PumpModifier), nameof(PumpModifier.MoveVolumeCapped));
      var turboMinSoundPitch = AccessTools.Property(typeof(TurboVolumePump), nameof(TurboVolumePump.MinOperatingSoundPitch))?.GetGetMethod();
      if (moveVolumeCapped == null || turboMinSoundPitch == null)
      {
        RealisticSaferAtmosphericsPlugin.Instance.LogError($"VolumePumpTranspiler: Unable to resolve methods. MoveVolumeCapped found: {moveVolumeCapped != null} MinOperatingSoundPitch found: {turboMinSoundPitch != null}");
        foreach (var instruction in instructions)
        {
          yield return instruction; // just pass through original code if we can't find the methods
        }
        yield break;
      }

      bool injected = false;
      foreach (CodeInstruction instruction in instructions)
      {
        // On first time we're calling a method called ToDouble
        if (injected == false && instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo methodInfo && methodInfo.Name == "MoveVolume")
        {
          // Check MinOperatingSoundPitch to determine if this is a turbo volume pump or not
          yield return new CodeInstruction(OpCodes.Ldarg_0);
          yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.Property(typeof(VolumePump), "MinOperatingSoundPitch")?.GetGetMethod());
          yield return new CodeInstruction(OpCodes.Ldc_R4, turboMinOperatingSoundPitch);
          yield return new CodeInstruction(OpCodes.Ceq); //Trick to avoid branching, result is 1 if turbo pump, 0 if normal pump
          yield return new CodeInstruction(OpCodes.Conv_R8);
          yield return new CodeInstruction(OpCodes.Ldc_R8, normalVolumePumpMaxDiff);
          yield return new CodeInstruction(OpCodes.Ldc_R8, turboVolumePumpMaxDiff);
          yield return new CodeInstruction(OpCodes.Sub); // Subtract to get difference between normal and turbo max diff
          yield return new CodeInstruction(OpCodes.Mul); // Multiply by 0 or 1 to select between normal and turbo max diff
          yield return new CodeInstruction(OpCodes.Ldc_R8, normalVolumePumpMaxDiff);
          yield return new CodeInstruction(OpCodes.Add); // Add back normal max diff to get final max diff value, should be either turbo or normal

          // Call our method, same as normal move volume but with extra parameter
          yield return new CodeInstruction(OpCodes.Call, moveVolumeCapped);
          injected = true; // only do this once
        }
        else
        {
          yield return instruction; // otherwise, just pass the instruction through unchanged
        }
      }
      if (!injected)
      {
        RealisticSaferAtmosphericsPlugin.Instance.LogError("VolumePumpTranspiler: Injection failed, pattern not found.");
      }
      else
      {
        RealisticSaferAtmosphericsPlugin.Instance.Log("VolumePumpTranspiler: Injection succeeded.");
      }
    }
  }
}
