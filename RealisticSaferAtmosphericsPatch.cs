using HarmonyLib;
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Util;
using Objects.Pipes;
using Assets.Scripts.Networks;

namespace RealisticSaferAtmospherics
{
  public static class PumpHelper
  {
    public static double getFlowRateModifier(double inputPressure, double outputPressure, double maxDiff)
    {
      double linearFalloff = 1 - (outputPressure - inputPressure) / maxDiff;
      double quadraticFalloff = Math.Sqrt(linearFalloff);
      return Math.Max(quadraticFalloff, 0.0);
    }

    public static double getFlowRateModifier(Atmosphere inputAtmos, Atmosphere outputAtmos, double maxDiff)
    {
      if (inputAtmos == null || outputAtmos == null)
      {
        return 0.0;
      }
      return getFlowRateModifier(inputAtmos.PressureGassesAndLiquids.ToDouble(), outputAtmos.PressureGassesAndLiquids.ToDouble(), maxDiff);
    }

    public static double getMixerFlowRateModifier(PipeNetwork inputPipes, PipeNetwork inputPipes2, PipeNetwork outputPipes, float ratio1, double maxDiff)
    {
      if (inputPipes == null || inputPipes2 == null || outputPipes == null)
      {
        return 0.0;
      }
      float ratio2 = 100.0f - ratio1;
      double in1Pressure = inputPipes.Atmosphere?.PressureGassesAndLiquids.ToDouble() ?? 0.0;
      double in2Pressure = inputPipes2.Atmosphere?.PressureGassesAndLiquids.ToDouble() ?? 0.0;
      double outPressure = outputPipes.Atmosphere?.PressureGassesAndLiquids.ToDouble() ?? 0.0;
      // Use average of input pressures to determine flow rate modifier
      double effectivePressure = 100.0 * Math.Min(in1Pressure / Math.Max(ratio1, 0.01f), in2Pressure / Math.Max(ratio2, 0.01f));
      double multiplier = getFlowRateModifier(effectivePressure, outPressure, maxDiff);
      RealisticSaferAtmosphericsPlugin.Instance.Log($"Mixer flow rate calc: In1 {in1Pressure}kPa @ {ratio1}%, In2 {in2Pressure}kPa @ {ratio2}%, Effective {effectivePressure}kPa, Out {outPressure}kPa, Multiplier {multiplier}");
      return multiplier;
    }

    // Copied from AtmosphereHelper, flow rate tapers off as we approach max pressure diff
    public static void MoveVolumeCapped(Atmosphere inputAtmos, Atmosphere outputAtmos, VolumeLitres volume, AtmosphereHelper.MatterState matterStateToMove, double maxPressureDiff)
    {
      double num = RocketMath.Clamp(volume / inputAtmos.GetVolume(matterStateToMove), VolumeLitres.Zero, inputAtmos.GetVolume(matterStateToMove)).ToDouble();
      // Added modifier to num based on pressure diff
      num *= getFlowRateModifier(inputAtmos, outputAtmos, maxPressureDiff);
      if (num <= 0.0)
      {
        return;
      }
      GasMixture gasMixture = inputAtmos.Remove(inputAtmos.GasMixture.GetTotalMoles(matterStateToMove) * num, matterStateToMove);
      outputAtmos.Add(gasMixture);
    }

    public static void MoveLiquidVolumeCapped(Atmosphere inputAtmos, Atmosphere outputAtmos, VolumeLitres volume, double maxPressureDiff)
    {
      volume *= new VolumeLitres(getFlowRateModifier(inputAtmos, outputAtmos, maxPressureDiff));
      outputAtmos.Add(AtmosphereHelper.RemoveLiquidVolume(inputAtmos, volume));
    }

    public static IEnumerable<CodeInstruction> TranspileMoveVolumeCapped(IEnumerable<CodeInstruction> instructions, double maxPressureDiff = 5000.0)
    {
      var moveVolumeCapped = AccessTools.Method(typeof(PumpHelper), nameof(MoveVolumeCapped));
      var moveLiquidVolumeCapped = AccessTools.Method(typeof(PumpHelper), nameof(MoveLiquidVolumeCapped));
      if (moveVolumeCapped == null || moveLiquidVolumeCapped == null)
      {
        RealisticSaferAtmosphericsPlugin.Instance.LogError($"VolumePumpTranspiler: Unable to resolve methods. MoveVolumeCapped found: {moveVolumeCapped != null} MoveLiquidVolumeCapped found: {moveLiquidVolumeCapped != null}");
      }
      foreach (var instruction in instructions)
      {
        if (instruction.opcode == OpCodes.Call)
        {
          if (instruction.operand is MethodInfo methodInfo && methodInfo.Name == "MoveVolume")
          {
            yield return new CodeInstruction(OpCodes.Ldc_R8, maxPressureDiff); // Push max pressure diff onto stack
            instruction.operand = moveVolumeCapped; // Replace call to MoveVolume with call to MoveVolumeCapped
          }
          if (instruction.operand is MethodInfo methodInfo2 && methodInfo2.Name == "MoveLiquidVolume")
          {
            yield return new CodeInstruction(OpCodes.Ldc_R8, maxPressureDiff); // Push max pressure diff onto stack
            instruction.operand = moveLiquidVolumeCapped; // Replace call to MoveLiquidVolume with call to MoveLiquidVolumeCapped
          }
        }
        yield return instruction; // otherwise, just pass the instruction through unchanged
      }
    }
  }


  [HarmonyPatch(typeof(PressureRegulator))]
  public static class PressureRegulatorPatch
  {
    [HarmonyPatch("OnAtmosphericTick")]
    [HarmonyPrefix]
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

  [HarmonyPatch(typeof(ActiveVent))]
  public static class ActiveVentPatch
  {
    [HarmonyPatch("PumpGasToPipe")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> ActiveVentTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return instructions;
    }
  }

  [HarmonyPatch(typeof(AdvancedFurnace))]
  public static class AdvancedFurnacePatch
  {
    [HarmonyPatch("HandleGasInput")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> AdvancedFurnaceTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return PumpHelper.TranspileMoveVolumeCapped(instructions, 40000.0);
    }
  }
  [HarmonyPatch(typeof(DeviceAtmospherics))]
  public static class DeviceAtmosphericsPatch
  {
    [HarmonyPatch("MoveVolume")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> DeviceAtmosphericsTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return PumpHelper.TranspileMoveVolumeCapped(instructions);
    }
  }

  [HarmonyPatch(typeof(IndustrialBurner))]
  public static class IndustrialBurnerPatch
  {
    [HarmonyPatch("HandleGasInput")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> IndustrialBurnerTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return PumpHelper.TranspileMoveVolumeCapped(instructions);
    }
  }

  [HarmonyPatch(typeof(LiquidRocketEngine))]
  public static class LiquidRocketEnginePatch
  {
    [HarmonyPatch("MovePropellant")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> LiquidRocketEngineTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return PumpHelper.TranspileMoveVolumeCapped(instructions, 40000.0);
    }
  }

  [HarmonyPatch(typeof(VolumePump))]
  public static class VolumePumpPatch

  {
    [HarmonyPatch("MoveAtmosphere")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> VolumePumpTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      const float turboMinOperatingSoundPitch = 0.75f; // Using sound pitch to differentiate pump types
      const double normalVolumePumpMaxDiff = 10000.0; // 10 MPa cap for volume pumps
      const double turboVolumePumpMaxDiff = 40000.0; // 40 MPa cap for turbo volume pumps
      const double turboNormalDiff = turboVolumePumpMaxDiff - normalVolumePumpMaxDiff;

      var moveVolumeCapped = AccessTools.Method(typeof(PumpHelper), nameof(PumpHelper.MoveVolumeCapped));
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
          yield return new CodeInstruction(OpCodes.Ldc_R8, turboNormalDiff);
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
    }
  }

  [HarmonyPatch(typeof(PressureFedLiquidEngine))]
  public static class PressureFedLiquidEnginePatch
  {
    [HarmonyPatch("MovePropellant")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> PressureFedLiquidEngineTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return PumpHelper.TranspileMoveVolumeCapped(instructions, 40000.0);
    }
  }

  [HarmonyPatch(typeof(PumpedLiquidEngine))]
  public static class PumpedLiquidEnginePatch
  {
    [HarmonyPatch("MovePropellant")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> PumpedLiquidEngineTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return PumpHelper.TranspileMoveVolumeCapped(instructions, 40000.0);
    }
  }

  [HarmonyPatch(typeof(Mixer))]
  public static class MixerPatch
  {
    [HarmonyPatch("OnAtmosphericTick")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> MixerTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
    {
      const double maxPressureDiff = 5000.0; // 5 MPa cap for mixers
      return new CodeMatcher(instructions, il)
        .MatchForward(false,
          new CodeMatch(OpCodes.Ldsfld, AccessTools.Field(typeof(MoleQuantity), "Zero")),
          new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(MoleQuantity), "op_GreaterThan", new Type[] { typeof(MoleQuantity), typeof(MoleQuantity) })),
          new CodeMatch((i) => i.opcode == OpCodes.Brfalse),
          new CodeMatch((i) => i.opcode == OpCodes.Ldloca_S)
        )
        .ThrowIfNotMatch("MixerTranspiler: Pattern not found")
        .Repeat(matcher =>
        {
          matcher
            .Advance(8)
            .InsertAndAdvance(
              new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(MoleQuantity), "_value")),
              new CodeInstruction(OpCodes.Ldarg_0),
              new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(DeviceInputOutput), "InputNetwork")),
              new CodeInstruction(OpCodes.Ldarg_0),
              new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(DeviceInputOutput), "InputNetwork2")),
              new CodeInstruction(OpCodes.Ldarg_0),
              new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(DeviceInputOutput), "OutputNetwork")),
              new CodeInstruction(OpCodes.Ldarg_0),
              new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Mixer), "Ratio1")),
              new CodeInstruction(OpCodes.Ldc_R8, maxPressureDiff),
              new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PumpHelper), nameof(PumpHelper.getMixerFlowRateModifier))),
              new CodeInstruction(OpCodes.Mul),
              new CodeInstruction(OpCodes.Newobj, AccessTools.Constructor(typeof(MoleQuantity), new Type[] { typeof(double) }))
          );
        }
        )
        .InstructionEnumeration();
    }
  }
}

