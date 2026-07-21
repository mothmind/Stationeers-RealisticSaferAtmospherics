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
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects;
namespace RealisticSaferAtmospherics
{
  public static class Logger
  {
    public static void Log(string message)
    {
      RealisticSaferAtmosphericsPlugin.Instance.Log(message);
    }
    public static void LogWarning(string message)
    {
      RealisticSaferAtmosphericsPlugin.Instance.LogWarning(message);
    }
    public static void LogError(string message)
    {
      RealisticSaferAtmosphericsPlugin.Instance.LogError(message);
    }
  }

  public static class FlowMath
  {
    public static double getFlowRateModifier(double inputPressure, double outputPressure, double maxDiff)
    {
      double linearFalloff = 1 - (outputPressure - inputPressure) / maxDiff;
      double quadraticFalloff = Math.Sqrt(Math.Max(linearFalloff, 0.0));
      return quadraticFalloff;
    }

    public static double getFlowRateModifier(Atmosphere inputAtmos, Atmosphere outputAtmos, double maxDiff)
    {
      if (inputAtmos == null || outputAtmos == null)
      {
        return 0.0;
      }
      return getFlowRateModifier(inputAtmos.PressureGassesAndLiquids.ToDouble(), outputAtmos.PressureGassesAndLiquids.ToDouble(), maxDiff);
    }

    public static double getFlowRateModifier(Atmosphere inputAtmos, PipeNetwork outputAtmos, double maxDiff)
    {
      if (inputAtmos == null || outputAtmos == null)
      {
        return 0.0;
      }
      return getFlowRateModifier(inputAtmos.PressureGassesAndLiquids.ToDouble(), outputAtmos.Atmosphere.PressureGassesAndLiquids.ToDouble(), maxDiff);
    }

    public static double getFlowRateModifier(PipeNetwork inputPipes, PipeNetwork outputPipes, double maxDiff)
    {
      if (inputPipes == null || outputPipes == null)
      {
        return 0.0;
      }
      return getFlowRateModifier(inputPipes.Atmosphere.PressureGassesAndLiquids.ToDouble(), outputPipes.Atmosphere.PressureGassesAndLiquids.ToDouble(), maxDiff);
    }

    public static double getMixerFlowRateModifier(PipeNetwork inputPipes, PipeNetwork inputPipes2, PipeNetwork outputPipes, float ratio1, double maxDiff)
    {
      if (inputPipes == null || inputPipes2 == null || outputPipes == null)
      {
        return 0.0;
      }
      float ratio2 = 100.0f - ratio1;
      double in1Pressure = inputPipes.Atmosphere.PressureGassesAndLiquids.ToDouble();
      double in2Pressure = inputPipes2.Atmosphere.PressureGassesAndLiquids.ToDouble();
      double outPressure = outputPipes.Atmosphere.PressureGassesAndLiquids.ToDouble();
      // Use average of input pressures to determine flow rate modifier
      double effectivePressure = 100.0 * Math.Min(in1Pressure / Math.Max(ratio1, 0.01f), in2Pressure / Math.Max(ratio2, 0.01f));
      double multiplier = getFlowRateModifier(effectivePressure, outPressure, maxDiff);
      return multiplier;
    }
  }


  public static class TranspilerHelpers
  {
    public static GasMixture TakeNormalisedGasPressureScaledCapped(Atmosphere inputAtmosphere, PressurekPa basePressurePerTick, PressurekPa inputPressureDelta, out MoleQuantity transferMoles, AtmosphereHelper.MatterState matterState = AtmosphereHelper.MatterState.All, float denominator = 3f, double maxPressureDiff = 5000.0)
    {
      double inputPressure = inputAtmosphere.PressureGassesAndLiquids.ToDouble();
      double outputPressure = inputPressure - inputPressureDelta.ToDouble();
      double modifier = FlowMath.getFlowRateModifier(inputPressure, outputPressure, maxPressureDiff);
      PressurekPa outMax = Chemistry.Limits.MAXPressureGasPipe / (double)denominator;
      inputPressureDelta = RocketMath.Max(inputPressureDelta, PressurekPa.Zero);
      PressurekPa pressure = RocketMath.MapToScale(PressurekPa.Zero, Chemistry.Limits.MAXPressureGasPipe, basePressurePerTick, outMax, inputPressureDelta);
      transferMoles = IdealGas.Quantity(pressure, Chemistry.PipeVolume, inputAtmosphere.Temperature);
      transferMoles *= modifier;
      return inputAtmosphere.Remove(transferMoles, matterState);
    }

    // Copied from AtmosphereHelper, flow rate tapers off as we approach max pressure diff
    public static void MoveVolumeCapped(Atmosphere inputAtmos, Atmosphere outputAtmos, VolumeLitres volume, AtmosphereHelper.MatterState matterStateToMove, MoleQuantity minMolesToMove, double maxPressureDiff)
    {
      double num = RocketMath.Clamp(volume / inputAtmos.GetVolume(matterStateToMove), VolumeLitres.Zero, inputAtmos.GetVolume(matterStateToMove)).ToDouble();
      // Added modifier to num based on pressure diff
      num *= FlowMath.getFlowRateModifier(inputAtmos, outputAtmos, maxPressureDiff);
      if (num <= 0.0)
      {
        return;
      }
      GasMixture gasMixture = inputAtmos.Remove(inputAtmos.GasMixture.GetTotalMoles(matterStateToMove) * num, matterStateToMove);
      outputAtmos.Add(gasMixture);
    }

    public static void MoveLiquidVolumeCapped(Atmosphere inputAtmos, Atmosphere outputAtmos, VolumeLitres volume, double maxPressureDiff)
    {
      volume *= new VolumeLitres(FlowMath.getFlowRateModifier(inputAtmos, outputAtmos, maxPressureDiff));
      outputAtmos.Add(AtmosphereHelper.RemoveLiquidVolume(inputAtmos, volume));
    }

    public static IEnumerable<CodeInstruction> TranspileMoveVolumeCapped(IEnumerable<CodeInstruction> instructions, double maxPressureDiff = 5000.0)
    {
      var moveVolumeCapped = AccessTools.Method(typeof(TranspilerHelpers), nameof(TranspilerHelpers.MoveVolumeCapped));
      var moveLiquidVolumeCapped = AccessTools.Method(typeof(TranspilerHelpers), nameof(TranspilerHelpers.MoveLiquidVolumeCapped));
      if (moveVolumeCapped == null || moveLiquidVolumeCapped == null)
      {
        Logger.LogError($"VolumePumpTranspiler: Unable to resolve methods. MoveVolumeCapped found: {moveVolumeCapped != null} MoveLiquidVolumeCapped found: {moveLiquidVolumeCapped != null}");
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

    public static IEnumerable<CodeInstruction> TranspileTakeNormalisedGasPressureScaledCapped(IEnumerable<CodeInstruction> instructions, double maxPressureDiff = 5000.0)
    {
      return new CodeMatcher(instructions)
        .MatchForward(false,
          new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(AtmosphereHelper), "TakeNormalisedGasPressureScaled", new Type[] { typeof(Atmosphere), typeof(PressurekPa), typeof(PressurekPa), typeof(MoleQuantity).MakeByRefType(), typeof(AtmosphereHelper.MatterState), typeof(float) }))
        )
        .ThrowIfNotMatch("TakeNormalisedGasPressureScaledCapped: Pattern not found")
        .InsertAndAdvance(
          new CodeInstruction(OpCodes.Ldc_R8, maxPressureDiff)
        )
        .SetInstructionAndAdvance(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TranspilerHelpers), nameof(TranspilerHelpers.TakeNormalisedGasPressureScaledCapped))))
        .InstructionEnumeration();
    }

    public static IEnumerable<CodeInstruction> TranspilePoweredVent(IEnumerable<CodeInstruction> instructions, bool succMode, double maxPressureDiff, bool staticMethod = false)
    {
      // First arg is World, second is Pipe.
      //   First op is Input, second is Output, so succMode is low to high, blowMode is high to low
      OpCode firstOp = succMode ? OpCodes.Ldarg_1 : OpCodes.Ldarg_2;
      OpCode secondOp = succMode ? OpCodes.Ldarg_2 : OpCodes.Ldarg_1;
      if (staticMethod)
      { // Static methods have args shifted down by 1
        firstOp = succMode ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1;
        secondOp = succMode ? OpCodes.Ldarg_1 : OpCodes.Ldarg_0;
      }
      return new CodeMatcher(instructions)
          .MatchForward(false,
            new CodeMatch(OpCodes.Callvirt, AccessTools.Method(typeof(Atmosphere), "Remove", new Type[] { typeof(MoleQuantity), typeof(AtmosphereHelper.MatterState) }))
          )
          .ThrowIfNotMatch("PoweredVentBlowTranspiler: Pattern not found")
          .Advance(-1)
          .InsertAndAdvance(
            new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(MoleQuantity), "_value")),
            new CodeInstruction(firstOp), // ldarg1 is World, ldarg2 is Pipe
            new CodeInstruction(secondOp), // First arg is input, second is output
            new CodeInstruction(OpCodes.Ldc_R8, maxPressureDiff),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FlowMath), nameof(FlowMath.getFlowRateModifier), new Type[] { typeof(Atmosphere), typeof(Atmosphere), typeof(double) })),
            new CodeInstruction(OpCodes.Mul),
            new CodeInstruction(OpCodes.Newobj, AccessTools.Constructor(typeof(MoleQuantity), new Type[] { typeof(double) }))
          )
          .InstructionEnumeration();
    }
  }

  [HarmonyPatch(typeof(PressureRegulator))]
  public static class PressureRegulatorPatch
  {
    [HarmonyPatch("Awake")]
    [HarmonyPrefix]
    static private void PressureRegulatorAwake(PressureRegulator __instance)
    {
      __instance.UsedPower = 5.0f; // Plugging in allows data connection at minor cost of power
      var pressurePerTickProperty = AccessTools.Field(typeof(PressureRegulator), "pressurePerTick");
      if (pressurePerTickProperty != null)
      {
        pressurePerTickProperty.SetValue(__instance, 0.0f); // Pressure Regulator only goes high to low
      }
    }

    [HarmonyPatch("OnAtmosphericTick")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> PressureRegulatorTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return new CodeMatcher(instructions)
        .MatchForward(false,
          new CodeMatch(OpCodes.Ldarg_0),
          new CodeMatch(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Thing), "Powered")),
          new CodeMatch(OpCodes.Brfalse)
        )
        .ThrowIfNotMatch("PressureRegulatorTranspiler: Pattern not found")
        .SetAndAdvance(OpCodes.Nop, null)
        .SetAndAdvance(OpCodes.Nop, null)
        .SetAndAdvance(OpCodes.Nop, null)
        .InstructionEnumeration();
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

  [HarmonyPatch(typeof(AdvancedFurnace))]
  public static class AdvancedFurnacePatch
  {
    [HarmonyPatch("HandleGasInput")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> AdvancedFurnaceTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return TranspilerHelpers.TranspileMoveVolumeCapped(instructions, 40000.0);
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

      var moveVolumeCapped = AccessTools.Method(typeof(TranspilerHelpers), nameof(TranspilerHelpers.MoveVolumeCapped));
      var turboMinSoundPitch = AccessTools.Property(typeof(TurboVolumePump), nameof(TurboVolumePump.MinOperatingSoundPitch)).GetGetMethod();
      if (moveVolumeCapped == null || turboMinSoundPitch == null)
      {
        Logger.LogError($"VolumePumpTranspiler: Unable to resolve methods. MoveVolumeCapped found: {moveVolumeCapped != null} MinOperatingSoundPitch found: {turboMinSoundPitch != null}");
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
          yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.Property(typeof(VolumePump), "MinOperatingSoundPitch").GetGetMethod());
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
        Logger.LogError("VolumePumpTranspiler: Injection failed, pattern not found.");
      }
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
              new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FlowMath), nameof(FlowMath.getMixerFlowRateModifier))),
              new CodeInstruction(OpCodes.Mul),
              new CodeInstruction(OpCodes.Newobj, AccessTools.Constructor(typeof(MoleQuantity), new Type[] { typeof(double) }))
          );
        }
        )
        .InstructionEnumeration();
    }
  }

  [HarmonyPatch(typeof(AirConditioner))]
  public static class AirConditionerPatch
  {
    [HarmonyPatch("OnAtmosphericTick")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> AirConditionerTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      const double maxPressureDiff = 5000.0; // 5 MPa cap for air conditioners
      return new CodeMatcher(instructions)
        .MatchForward(false,
          new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(IdealGas), nameof(IdealGas.Quantity), new Type[] { typeof(PressurekPa), typeof(VolumeLitres), typeof(TemperatureKelvin) }))
        )
        .ThrowIfNotMatch("AirConditionerTranspiler: Pattern not found")
        .Advance(1)
        .InsertAndAdvance(
          new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(MoleQuantity), "_value")),
          new CodeInstruction(OpCodes.Ldarg_0),
          new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(DeviceInputOutput), "InputNetwork")),
          new CodeInstruction(OpCodes.Ldarg_0),
          new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(DeviceInputOutput), "OutputNetwork")),
          new CodeInstruction(OpCodes.Ldc_R8, maxPressureDiff),
          new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FlowMath), nameof(FlowMath.getFlowRateModifier), new Type[] { typeof(PipeNetwork), typeof(PipeNetwork), typeof(double) })),
          new CodeInstruction(OpCodes.Mul),
          new CodeInstruction(OpCodes.Newobj, AccessTools.Constructor(typeof(MoleQuantity), new Type[] { typeof(double) }))
      )
      .InstructionEnumeration();
    }
  }

  [HarmonyPatch(typeof(FiltrationMachineBase))]
  public static class FiltrationUnitPatch
  {
    [HarmonyPatch("OnAtmosphericTick")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> FiltrationUnitTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      const double maxPressureDiff = 5000.0;
      return TranspilerHelpers.TranspileTakeNormalisedGasPressureScaledCapped(instructions, maxPressureDiff);
    }
  }

  [HarmonyPatch(typeof(InternalCombustion))]
  public static class InternalCombustionPatch
  {
    [HarmonyPatch("HandleGasOutput")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> InternalCombustionTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      const double maxPressureDiff = 5000.0; // 5 MPa cap for combustion centrifug
      return new CodeMatcher(instructions)
        .MatchForward(false,
          new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(RocketMath), nameof(RocketMath.Min), new Type[] { typeof(MoleQuantity), typeof(MoleQuantity) }))
        )
        .ThrowIfNotMatch("InternalCombustionTranspiler: Pattern not found")
        .Advance(1)
        .InsertAndAdvance(
          new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(MoleQuantity), "_value")),
          new CodeInstruction(OpCodes.Ldarg_0),
          new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Thing), "InternalAtmosphere")),
          new CodeInstruction(OpCodes.Ldarg_0),
          new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(DeviceInputOutput), "OutputNetwork")),
          new CodeInstruction(OpCodes.Ldc_R8, maxPressureDiff),
          new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FlowMath), nameof(FlowMath.getFlowRateModifier), new Type[] { typeof(Atmosphere), typeof(PipeNetwork), typeof(double) })),
          new CodeInstruction(OpCodes.Mul),
          new CodeInstruction(OpCodes.Newobj, AccessTools.Constructor(typeof(MoleQuantity), new Type[] { typeof(double) }))
      )
      .InstructionEnumeration();
    }
  }

  [HarmonyPatch(typeof(ActiveVent))]
  public static class ActiveVentPatch
  {
    const double maxPressureDiff = 5000.0; // 5 MPa cap for active vents
    [HarmonyPatch("PumpGasToWorld")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> ActiveVentBlowTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return TranspilerHelpers.TranspilePoweredVent(instructions, false, maxPressureDiff);
    }

    [HarmonyPatch("PumpGasToPipe")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> ActiveVentSuccTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return TranspilerHelpers.TranspilePoweredVent(instructions, true, maxPressureDiff);
    }
  }

  [HarmonyPatch(typeof(PoweredVent))]
  public static class PoweredVentPatch
  {
    const double maxPressureDiff = 10000.0; // 10 MPa cap for powered vents
    [HarmonyPatch("PumpGasToWorld")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> PoweredVentBlowTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return TranspilerHelpers.TranspilePoweredVent(instructions, false, maxPressureDiff);
    }

    [HarmonyPatch("PumpGasToPipe")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> PoweredVentSuccTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return TranspilerHelpers.TranspilePoweredVent(instructions, true, maxPressureDiff);
    }
  }


  [HarmonyPatch(typeof(MultiGridAtmospherics))]
  public static class MultiGridAtmosphericsPatch
  {
    const double maxPressureDiff = 10000.0; // 10 MPa cap for powered vents
    [HarmonyPatch("PumpGasToWorld")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> MultiPoweredVentBlowTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return TranspilerHelpers.TranspilePoweredVent(instructions, false, maxPressureDiff, true);
    }

    [HarmonyPatch("PumpGasToPipe")]
    [HarmonyTranspiler]
    static IEnumerable<CodeInstruction> MultiPoweredVentSuccTranspiler(IEnumerable<CodeInstruction> instructions)
    {
      return TranspilerHelpers.TranspilePoweredVent(instructions, true, maxPressureDiff, true);
    }
  }

}

