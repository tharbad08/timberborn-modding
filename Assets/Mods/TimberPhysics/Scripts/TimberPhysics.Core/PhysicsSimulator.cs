using Timberborn.SingletonSystem;
using UnityEngine;

namespace TimberPhysics.Core {
  internal class PhysicsSimulator : IUpdatableSingleton {

    private static readonly float FixedDeltaTime = 0.02f;
    private const int MaxSubstepsPerUpdate = 4;
    private readonly PhysicalObjectRegistry _physicalObjectRegistry;
    private float _timer;

    public PhysicsSimulator(PhysicalObjectRegistry physicalObjectRegistry) {
      _physicalObjectRegistry = physicalObjectRegistry;
    }

    public void UpdateSingleton() {
      if (Physics.simulationMode == SimulationMode.Script) {
        _timer += Time.deltaTime;

        var substeps = 0;
        while (_timer >= FixedDeltaTime && substeps < MaxSubstepsPerUpdate) {
          _timer -= FixedDeltaTime;
          _physicalObjectRegistry.StepAll(FixedDeltaTime);
          Physics.Simulate(FixedDeltaTime);
          substeps++;
        }

        if (_timer >= FixedDeltaTime) {
          _timer %= FixedDeltaTime;
        }
      }
    }

  }
}