using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace RealisticSaferAtmospherics
{
  [BepInPlugin("net.Moth.stationeers.RealisticSaferAtmospherics", "Realistic (Safer) Atmospherics", "1.1")]
  public class RealisticSaferAtmosphericsPlugin : BaseUnityPlugin
  {
    public static RealisticSaferAtmosphericsPlugin Instance;


    public void Log(string line)
    {
      Debug.Log("[RealisticSaferAtmospherics]: " + line);
    }

    public void LogWarning(string line)
    {
      Debug.LogWarning("[RealisticSaferAtmospherics]: " + line);
    }

    public void LogError(string line)
    {
      Debug.LogError("[RealisticSaferAtmospherics]: " + line);
    }

    void Awake()
    {
      RealisticSaferAtmosphericsPlugin.Instance = this;
      Log("Mothmod Initializing");

      try
      {
        // Harmony.DEBUG = true;
        var harmony = new Harmony("net.Moth.stationeers.RealisticSaferAtmospherics");
        harmony.PatchAll();
        Log("Patch succeeded");
      }
      catch (Exception e)
      {
        Log("Patch Failed");
        Log(e.ToString());
      }
    }
  }
}