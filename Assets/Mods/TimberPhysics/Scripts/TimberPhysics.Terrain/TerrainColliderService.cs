using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using Timberborn.Coordinates;
using Timberborn.MapIndexSystem;
using Timberborn.MapStateSystem;
using Timberborn.RootProviders;
using Timberborn.SingletonSystem;
using Timberborn.TerrainSystem;
using TimberPhysics.Layers;
using UnityEngine;

namespace TimberPhysics.Terrain {
  public class TerrainColliderService : ILoadableSingleton,
                                        IUnloadableSingleton {

    [UsedImplicitly]
    public static readonly string TerrainLayerName = "Terrain";
    public static int? TerrainLayerIndex { get; private set; }

    // Keep merging local so a terrain edit only needs to rebuild a small area.
    private const int ColliderChunkSize = 16;

    private readonly RootObjectProvider _rootObjectProvider;
    private readonly ColumnTerrainMap _columnTerrainMap;
    private readonly MapSize _mapSize;
    private readonly MapIndexService _mapIndexService;
    private readonly PhysicsLayerRegistry _physicsLayerRegistry;
    private readonly ITerrainService _terrainService;
    private GameObject _rootObject;

    // A chunk contains greedily merged boxes. Cells are merged only when their
    // terrain interval has exactly the same floor and ceiling, so the resulting
    // collider volume is identical to the original per-cell boxes.
    private readonly Dictionary<Vector2Int, List<BoxCollider>> _chunkColliders = new();

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

      SpawnColliders();
      _terrainService.PreTerrainHeightChanged += OnPreTerrainHeightChanged;
      _terrainService.TerrainHeightChanged += OnTerrainHeightChanged;
    }

    public void Unload() {
      TerrainLayerIndex = null;
    }

    internal void ToggleColliders() {
      _rootObject.SetActive(!_rootObject.activeSelf);
    }

    private void SpawnColliders() {
      for (var y = 0; y < _mapSize.TerrainSize.y; y += ColliderChunkSize) {
        for (var x = 0; x < _mapSize.TerrainSize.x; x += ColliderChunkSize) {
          SpawnChunk(new Vector2Int(x, y));
        }
      }
    }

    private void OnPreTerrainHeightChanged(object sender,
                                           TerrainHeightChangeEventArgs
                                               terrainHeightChangeEventArgs) {
      RemoveChunk(ChunkOrigin(terrainHeightChangeEventArgs.Change.Coordinates));
    }

    private void OnTerrainHeightChanged(object sender,
                                        TerrainHeightChangeEventArgs terrainHeightChangeEventArgs) {
      SpawnChunk(ChunkOrigin(terrainHeightChangeEventArgs.Change.Coordinates));
    }

    private Vector2Int ChunkOrigin(Vector2Int coordinates) {
      return new Vector2Int(
          coordinates.x / ColliderChunkSize * ColliderChunkSize,
          coordinates.y / ColliderChunkSize * ColliderChunkSize);
    }

    private void SpawnChunk(Vector2Int origin) {
      // Multiple terrain events in one chunk can recreate it more than once.
      // Always start from a clean chunk so stale merged boxes cannot survive.
      RemoveChunk(origin);

      var width = Math.Min(ColliderChunkSize, _mapSize.TerrainSize.x - origin.x);
      var height = Math.Min(ColliderChunkSize, _mapSize.TerrainSize.y - origin.y);
      if (width <= 0 || height <= 0) {
        return;
      }

      var occupancyByInterval = new Dictionary<(float Floor, float Ceiling), bool[,]>();

      for (var localY = 0; localY < height; localY++) {
        for (var localX = 0; localX < width; localX++) {
          var coordinates = new Vector2Int(origin.x + localX, origin.y + localY);
          var cellIndex = _mapIndexService.CellToIndex(coordinates);
          var columnCount = _columnTerrainMap.ColumnCount[cellIndex];

          for (var i = 0; i < columnCount; i++) {
            var index3D = cellIndex + i * _mapIndexService.VerticalStride;
            var terrainColumn = _columnTerrainMap.GetColumn(index3D);
            var floor = (float) terrainColumn.Floor;
            var ceiling = (float) terrainColumn.Ceiling;

            if (ceiling <= floor) {
              continue;
            }

            var key = (floor, ceiling);
            if (!occupancyByInterval.TryGetValue(key, out var occupancy)) {
              occupancy = new bool[width, height];
              occupancyByInterval.Add(key, occupancy);
            }

            occupancy[localX, localY] = true;
          }
        }
      }

      var colliders = new List<BoxCollider>();
      foreach (var pair in occupancyByInterval) {
        MergeInterval(origin, pair.Key.Floor, pair.Key.Ceiling, pair.Value, colliders);
      }

      _chunkColliders[origin] = colliders;
    }

    private void MergeInterval(Vector2Int origin,
                               float floor,
                               float ceiling,
                               bool[,] occupancy,
                               List<BoxCollider> colliders) {
      var width = occupancy.GetLength(0);
      var height = occupancy.GetLength(1);
      var consumed = new bool[width, height];

      for (var localY = 0; localY < height; localY++) {
        for (var localX = 0; localX < width; localX++) {
          if (!occupancy[localX, localY] || consumed[localX, localY]) {
            continue;
          }

          var runWidth = 1;
          while (localX + runWidth < width
                 && occupancy[localX + runWidth, localY]
                 && !consumed[localX + runWidth, localY]) {
            runWidth++;
          }

          var runHeight = 1;
          var canGrow = true;
          while (localY + runHeight < height && canGrow) {
            for (var x = 0; x < runWidth; x++) {
              if (!occupancy[localX + x, localY + runHeight]
                  || consumed[localX + x, localY + runHeight]) {
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
              origin.x + localX,
              origin.y + localY,
              runWidth,
              runHeight,
              floor,
              ceiling));
        }
      }
    }

    private BoxCollider SpawnMergedCollider(int startX,
                                            int startY,
                                            int width,
                                            int height,
                                            float floor,
                                            float ceiling) {
      var first = CoordinateSystem.GridToWorldCentered(
          new Vector3(startX, startY, floor));
      var last = CoordinateSystem.GridToWorldCentered(
          new Vector3(startX + width - 1, startY + height - 1, floor));

      var colliderHeight = ceiling - floor;
      var size = new Vector3(
          Mathf.Abs(last.x - first.x) + 1f,
          colliderHeight,
          Mathf.Abs(last.z - first.z) + 1f);

      var collider = _rootObject.AddComponent<BoxCollider>();
      collider.center = new Vector3(
          (first.x + last.x) / 2f,
          first.y + colliderHeight / 2f,
          (first.z + last.z) / 2f);
      collider.size = size;
      return collider;
    }

    private void RemoveChunk(Vector2Int origin) {
      if (!_chunkColliders.TryGetValue(origin, out var colliders)) {
        return;
      }

      foreach (var collider in colliders) {
        if (collider != null) {
          Object.Destroy(collider);
        }
      }

      _chunkColliders.Remove(origin);
    }

  }
}
