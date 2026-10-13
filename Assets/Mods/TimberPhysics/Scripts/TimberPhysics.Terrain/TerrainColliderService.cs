using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using Timberborn.Coordinates;
using Timberborn.MapIndexSystem;
using Timberborn.MapStateSystem;
using Timberborn.RootProviders;
using Timberborn.SingletonSystem;
using Timberborn.TerrainSystem;
using TimberPhysics.Core;
using TimberPhysics.Layers;
using UnityEngine;

namespace TimberPhysics.Terrain {
  public class TerrainColliderService : ILoadableSingleton, IUnloadableSingleton {

    [UsedImplicitly]
    public static readonly string TerrainLayerName = "Terrain";
    public static int? TerrainLayerIndex { get; private set; }

    private const int ColliderChunkSize = 32;

    private readonly RootObjectProvider _rootObjectProvider;
    private readonly ColumnTerrainMap _columnTerrainMap;
    private readonly MapSize _mapSize;
    private readonly MapIndexService _mapIndexService;
    private readonly PhysicsLayerRegistry _physicsLayerRegistry;
    private readonly ITerrainService _terrainService;
    private GameObject _rootObject;
    private bool _loaded;
    private bool _useVanillaColliders;

    // Every live collider is owned by exactly one chunk. All replacements
    // are built while the old set is still tracked.
    private readonly Dictionary<Vector2Int, List<BoxCollider>> _chunkColliders = new();
    private readonly HashSet<Vector2Int> _dirtyChunks = new();

    public TerrainColliderService(RootObjectProvider rootObjectProvider,
                                  ColumnTerrainMap columnTerrainMap,
                                  MapSize mapSize,
                                  MapIndexService mapIndexService,
                                  PhysicsLayerRegistry physicsLayerRegistry,
                                  ITerrainService terrainService) {
      _rootObjectProvider = rootObjectProvider;
      _columnTerrainMap = columnTerrainMap;
      _mapSize = mapSize;
      _mapIndexService = mapIndexService;
      _physicsLayerRegistry = physicsLayerRegistry;
      _terrainService = terrainService;
    }

    public void Load() {
      _rootObject = _rootObjectProvider.CreateRootObject("TerrainColliders");
      _rootObject.isStatic = true;
      _physicsLayerRegistry.AssignGameObjectToLayer(_rootObject, TerrainLayerName);
      if (_physicsLayerRegistry.TryGetLayerIndex(TerrainLayerName, out var terrainLayerIndex)) {
        TerrainLayerIndex = terrainLayerIndex;
      } else {
        Debug.LogWarning($"Failed to assign terrain colliders to layer {TerrainLayerName}. "
                         + "Raycasts may not work correctly.");
      }

      try {
        SpawnColliders();
      } catch (Exception ex) {
        Debug.LogError($"TimberPhysics merged terrain initialization failed: {ex}");
        RestoreVanillaColliders();
      }

      _terrainService.PreTerrainHeightChanged += OnPreTerrainHeightChanged;
      _terrainService.TerrainHeightChanged += OnTerrainHeightChanged;
      PhysicsSimulator.BeforePhysicsUpdate += FlushDirtyChunks;
      _loaded = true;
    }

    public void Unload() {
      _loaded = false;
      PhysicsSimulator.BeforePhysicsUpdate -= FlushDirtyChunks;
      _terrainService.PreTerrainHeightChanged -= OnPreTerrainHeightChanged;
      _terrainService.TerrainHeightChanged -= OnTerrainHeightChanged;
      _dirtyChunks.Clear();
      _chunkColliders.Clear();
      TerrainLayerIndex = null;
    }

    internal void ToggleColliders() {
      if (_rootObject != null) {
        _rootObject.SetActive(!_rootObject.activeSelf);
      }
    }

    private void SpawnColliders() {
      foreach (var origin in AllChunkOrigins()) {
        _chunkColliders.Add(origin, BuildChunk(origin, false));
      }
    }

    private IEnumerable<Vector2Int> AllChunkOrigins() {
      for (var y = 0; y < _mapSize.TerrainSize.y; y += ColliderChunkSize) {
        for (var x = 0; x < _mapSize.TerrainSize.x; x += ColliderChunkSize) {
          yield return new Vector2Int(x, y);
        }
      }
    }

    private void OnPreTerrainHeightChanged(object sender,
                                           TerrainHeightChangeEventArgs args) {
      if (_loaded) {
        _dirtyChunks.Add(ChunkOrigin(args.Change.Coordinates));
      }
    }

    private void OnTerrainHeightChanged(object sender,
                                        TerrainHeightChangeEventArgs args) {
      if (_loaded) {
        _dirtyChunks.Add(ChunkOrigin(args.Change.Coordinates));
      }
    }

    private static Vector2Int ChunkOrigin(Vector2Int coordinates) {
      return new Vector2Int(
          coordinates.x / ColliderChunkSize * ColliderChunkSize,
          coordinates.y / ColliderChunkSize * ColliderChunkSize);
    }

    private void FlushDirtyChunks() {
      if (!_loaded || _dirtyChunks.Count == 0) {
        return;
      }

      // Drain once before physics, rather than rebuilding for every dynamite
      // / terrain event. Never mutate the hash set during enumeration.
      var pending = new List<Vector2Int>(_dirtyChunks);
      _dirtyChunks.Clear();
      try {
        foreach (var origin in pending) {
          var replacement = BuildChunk(origin, _useVanillaColliders);
          ReplaceChunk(origin, replacement);
        }
      } catch (Exception ex) {
        Debug.LogError($"TimberPhysics terrain rebuild failed; restoring per-cell colliders: {ex}");
        RestoreVanillaColliders();
      }
    }

    private void ReplaceChunk(Vector2Int origin, List<BoxCollider> replacement) {
      // BuildChunk completes before any old colliders are removed.
      if (_chunkColliders.TryGetValue(origin, out var previous)) {
        DestroyColliders(previous);
      }
      _chunkColliders[origin] = replacement;
    }

    private void RestoreVanillaColliders() {
      _useVanillaColliders = true;
      _dirtyChunks.Clear();

      // Build a complete new per-cell set before destroying existing geometry.
      // If even the fallback cannot be built, keep existing colliders and
      // propagate a loud error rather than silently leaving an empty map.
      var replacements = new Dictionary<Vector2Int, List<BoxCollider>>();
      try {
        foreach (var origin in AllChunkOrigins()) {
          replacements.Add(origin, BuildChunk(origin, true));
        }
      } catch {
        foreach (var replacement in replacements.Values) {
          DestroyColliders(replacement);
        }
        throw;
      }

      foreach (var previous in _chunkColliders.Values) {
        DestroyColliders(previous);
      }
      _chunkColliders.Clear();
      foreach (var pair in replacements) {
        _chunkColliders.Add(pair.Key, pair.Value);
      }
      Debug.LogWarning("TimberPhysics: collider merger disabled for this map; full per-cell colliders restored.");
    }

    private List<BoxCollider> BuildChunk(Vector2Int origin, bool individual) {
      var width = Math.Min(ColliderChunkSize, _mapSize.TerrainSize.x - origin.x);
      var height = Math.Min(ColliderChunkSize, _mapSize.TerrainSize.y - origin.y);
      var result = new List<BoxCollider>();
      if (width <= 0 || height <= 0) {
        return result;
      }

      try {
        if (individual) {
          for (var localY = 0; localY < height; localY++) {
            for (var localX = 0; localX < width; localX++) {
              var x = origin.x + localX;
              var y = origin.y + localY;
              VisitColumns(x, y, (floor, ceiling) =>
                  result.Add(SpawnMergedCollider(x, y, 1, 1, floor, ceiling)));
            }
          }
        } else {
          var occupancyByInterval = new Dictionary<(float Floor, float Ceiling), bool[,]>();
          for (var localY = 0; localY < height; localY++) {
            for (var localX = 0; localX < width; localX++) {
              var x = origin.x + localX;
              var y = origin.y + localY;
              VisitColumns(x, y, (floor, ceiling) => {
                var key = (floor, ceiling);
                if (!occupancyByInterval.TryGetValue(key, out var occupancy)) {
                  occupancy = new bool[width, height];
                  occupancyByInterval.Add(key, occupancy);
                }
                occupancy[localX, localY] = true;
              });
            }
          }

          foreach (var pair in occupancyByInterval) {
            MergeInterval(origin, pair.Key.Floor, pair.Key.Ceiling, pair.Value, result);
          }
        }
        return result;
      } catch {
        DestroyColliders(result);
        throw;
      }
    }

    private void VisitColumns(int x, int y, Action<float, float> visitor) {
      var cellIndex = _mapIndexService.CellToIndex(new Vector2Int(x, y));
      var columnCount = _columnTerrainMap.ColumnCount[cellIndex];
      for (var i = 0; i < columnCount; i++) {
        var index3D = cellIndex + i * _mapIndexService.VerticalStride;
        var column = _columnTerrainMap.GetColumn(index3D);
        var floor = (float) column.Floor;
        var ceiling = (float) column.Ceiling;
        if (ceiling > floor) {
          visitor(floor, ceiling);
        }
      }
    }

    private void MergeInterval(Vector2Int origin, float floor, float ceiling,
                               bool[,] occupancy, List<BoxCollider> colliders) {
      var width = occupancy.GetLength(0);
      var height = occupancy.GetLength(1);
      var consumed = new bool[width, height];
      for (var localY = 0; localY < height; localY++) {
        for (var localX = 0; localX < width; localX++) {
          if (!occupancy[localX, localY] || consumed[localX, localY]) {
            continue;
          }
          var runWidth = 1;
          while (localX + runWidth < width &&
                 occupancy[localX + runWidth, localY] &&
                 !consumed[localX + runWidth, localY]) {
            runWidth++;
          }
          var runHeight = 1;
          var canGrow = true;
          while (localY + runHeight < height && canGrow) {
            for (var x = 0; x < runWidth; x++) {
              if (!occupancy[localX + x, localY + runHeight] ||
                  consumed[localX + x, localY + runHeight]) {
                canGrow = false;
                break;
              }
            }
            if (canGrow) {
              runHeight++;
            }
          }
          for (var y = 0; y < runHeight; y++) {
            for (var x = 0; x < runWidth; x++) {
              consumed[localX + x, localY + y] = true;
            }
          }
          colliders.Add(SpawnMergedCollider(
              origin.x + localX, origin.y + localY,
              runWidth, runHeight, floor, ceiling));
        }
      }
    }

    private BoxCollider SpawnMergedCollider(int startX, int startY,
                                            int width, int height,
                                            float floor, float ceiling) {
      var first = CoordinateSystem.GridToWorldCentered(
          new Vector3(startX, startY, floor));
      var last = CoordinateSystem.GridToWorldCentered(
          new Vector3(startX + width - 1, startY + height - 1, floor));
      var colliderHeight = ceiling - floor;
      var collider = _rootObject.AddComponent<BoxCollider>();
      try {
        collider.center = new Vector3(
            (first.x + last.x) / 2f,
            first.y + colliderHeight / 2f,
            (first.z + last.z) / 2f);
        collider.size = new Vector3(
            Mathf.Abs(last.x - first.x) + 1f,
            colliderHeight,
            Mathf.Abs(last.z - first.z) + 1f);
        return collider;
      } catch {
        collider.enabled = false;
        UnityEngine.Object.Destroy(collider);
        throw;
      }
    }

    private static void DestroyColliders(List<BoxCollider> colliders) {
      foreach (var collider in colliders) {
        if (collider != null) {
          // Destroy is deferred to end-of-frame; disabling now avoids old and
          // replacement terrain simultaneously participating in PhysX.
          collider.enabled = false;
          UnityEngine.Object.Destroy(collider);
        }
      }
    }
  }
}
