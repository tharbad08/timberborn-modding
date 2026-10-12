using System;
using System.Diagnostics;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace TimberPhysics.Core {
  internal class PhysicsSimulator : IUpdatableSingleton {

    private const float FixedDeltaTime = 0.02f;
    private const int MaxSubstepsPerUpdate = 4;
    // The budget limits *completed* PhysX work, not the duration of one native step.
    private const double MaxPhysicsWallBudgetMs = 50.0;

    // Terrain must finish batched collider updates before any new physics step.
    // Subscribing services should also update independently if another Harmony
    // prefix replaces UpdateSingleton rather than calling the original method.
    public static event Action BeforePhysicsUpdate;

    private readonly PhysicalObjectRegistry _physicalObjectRegistry;
    private float _timer;

    public PhysicsSimulator(PhysicalObjectRegistry physicalObjectRegistry) {
      _physicalObjectRegistry = physicalObjectRegistry;
    }

    public void UpdateSingleton() {
      // Also flush while simulation is paused or managed by another simulation
      // mode, so collision queries do not retain obsolete terrain geometry.
      BeforePhysicsUpdate?.Invoke();

      if (Physics.simulationMode != SimulationMode.Script) {
        return;
      }

      _timer += Time.deltaTime;
      var substeps = 0;
      long simulateTicks = 0;

      while (_timer >= FixedDeltaTime && substeps < MaxSubstepsPerUpdate) {
        // Preserve the original timer/StepAll/Simulate ordering.
        _timer -= FixedDeltaTime;
        _physicalObjectRegistry.StepAll(FixedDeltaTime);

        var stepStarted = Stopwatch.GetTimestamp();
        Physics.Simulate(FixedDeltaTime);
        simulateTicks += Stopwatch.GetTimestamp() - stepStarted;
        substeps++;

        if (simulateTicks * 1000.0 / Stopwatch.Frequency >= MaxPhysicsWallBudgetMs) {
          break;
        }
      }

      if (_timer >= FixedDeltaTime) {
        // Intentionally drop excess catch-up time under load to keep the game
        // responsive. This does not reproduce every elapsed physical substep.
        _timer %= FixedDeltaTime;
      }
    }

  }
}
