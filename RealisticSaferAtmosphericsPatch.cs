using HarmonyLib;
using JetBrains.Annotations;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Atmospherics;
using System.Collections.Generic;
using System.Reflection.Emit;
using Objects.Pipes;
using System.Reflection;
using System;

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


  [HarmonyPatch(typeof(AtmosphereHelper))]
  public static class VolumePumpPatch
  {
    [HarmonyPatch("MoveVolume")]
    [HarmonyTranspiler]
    [UsedImplicitly]
    static IEnumerable<CodeInstruction> VolumePumpTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      const double maxDifferential = 10000.0; // 10 MPa for volume pumps

      RealisticSaferAtmosphericsPlugin.Instance.Log("Trying to patch AtmosphereHelper.MoveVolume...");

      var toDouble = AccessTools.Field(typeof(PressurekPa), "_value");
      var getPressure = AccessTools.Property(typeof(Atmosphere), nameof(Atmosphere.PressureGassesAndLiquids)).GetGetMethod();
      var getFlowRateModifier = AccessTools.Method(typeof(PumpModifier), nameof(PumpModifier.getFlowRateModifier));
      if (toDouble == null || getPressure == null || getFlowRateModifier == null)
      {
        RealisticSaferAtmosphericsPlugin.Instance.LogError($"VolumePumpTranspiler: Unable to resolve methods. ToDouble found: {toDouble != null}, getPressure found: {getPressure != null}, getFlowRateModifier found: {getFlowRateModifier != null}");
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
        if (injected == false && instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo methodInfo && methodInfo.Name == "ToDouble")
        {
          // keep original ToDouble result on stack for later multiplication
          yield return instruction;

          // push input pressure as double
          yield return new CodeInstruction(OpCodes.Ldarg_0); // Load first arg (reference to Input Atmos)
          yield return new CodeInstruction(OpCodes.Call, getPressure); // Call GetPressure on the Input Atmos
          yield return new CodeInstruction(OpCodes.Ldfld, toDouble); // Convert to double
          // push output pressure as double
          yield return new CodeInstruction(OpCodes.Ldarg_1); // Load second arg (reference to Output Atmos)
          yield return new CodeInstruction(OpCodes.Call, getPressure); // Call GetPressure on the Output Atmos
          yield return new CodeInstruction(OpCodes.Ldfld, toDouble); // Convert to double
          // push max differential constant
          yield return new CodeInstruction(OpCodes.Ldc_R8, maxDifferential); // Load max differential constant
          // Call our method, pops 3 doubles, pushes 1 double
          yield return new CodeInstruction(OpCodes.Call, getFlowRateModifier);
          // multiply the result of ToDouble by the result of our method, next op should save to variable which gets reused by the rest of the function call
          yield return new CodeInstruction(OpCodes.Mul);
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
