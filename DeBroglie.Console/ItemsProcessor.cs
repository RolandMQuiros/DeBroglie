﻿using DeBroglie.Console.Config;
using DeBroglie.Console.Export;
using DeBroglie.Console.Import;
using DeBroglie.Constraints;
using DeBroglie.MagicaVoxel;
using DeBroglie.Models;
using DeBroglie.Topo;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace DeBroglie.Console
{

    public class ItemsProcessor
    {
        private readonly ISampleSetImporter loader;
        private readonly DeBroglieConfig config;

        public ItemsProcessor(ISampleSetImporter loader, DeBroglieConfig config)
        {
            this.loader = loader;
            this.config = config;
        }

        private SampleSet LoadSample()
        {
            if (config.Src == null)
                throw new Exception("Src sample should be set");
            var filename = Path.Combine(config.BaseDirectory, config.Src);
            System.Console.WriteLine($"Reading {filename}");

            var sampleSet = loader.Load(filename);
            sampleSet.Samples = sampleSet.Samples
                .Select(x => x.WithPeriodic(
                    config.PeriodicInputX,
                    config.PeriodicInputY,
                    config.PeriodicInputZ))
                    .ToArray();
            return sampleSet;
        }

        private SampleSet LoadFileSet()
        {
            if (config.Tiles == null)
                throw new Exception($"You must specify tile data when using SrcType {config.SrcType}.");

            var filenames = new Dictionary<Tile, string>();
            foreach (var tile in config.Tiles)
            {
                if (tile.Value == null)
                {
                    tile.Value = new Guid().ToString();
                }
                if (tile.Src == null)
                    throw new Exception($"All tiles must have a src set when using SrcType {config.SrcType}.");
                filenames[Parse(tile.Value)] = tile.Src;
            }

            if(filenames.Count == 0)
                throw new Exception($"Must supply at least one tile when using SrcType {config.SrcType}.");

            if (config.SrcType == SrcType.BitmapSet)
            {
                var bitmaps = filenames.ToDictionary(x => x.Key, x => new Bitmap(Path.Combine(config.BaseDirectory, x.Value)));
                var first = bitmaps.First().Value;
                return new SampleSet
                {
                    Directions = Directions.Cartesian2d,
                    Samples = new ITopoArray<Tile>[0],
                    ExportOptions = new BitmapSetExportOptions
                    {
                        Bitmaps = bitmaps,
                        TileWidth = first.Width,
                        TileHeight = first.Height,
                    }
                };
            }
            else if(config.SrcType == SrcType.VoxSet)
            {
                var subtiles = filenames.ToDictionary(x => x.Key, x => VoxUtils.Load(Path.Combine(config.BaseDirectory, x.Value)));
                var first = VoxUtils.ToTopoArray(subtiles.First().Value);
                return new SampleSet
                {
                    Directions = Directions.Cartesian3d,
                    Samples = new ITopoArray<Tile>[0],
                    ExportOptions = new VoxSetExportOptions
                    {
                        Template = subtiles.First().Value,
                        SubTiles = subtiles,
                        TileWidth = first.Topology.Width,
                        TileHeight = first.Topology.Height,
                        TileDepth = first.Topology.Depth,
                    }
                };
            }
            else
            {
                throw new NotImplementedException($"Unrecognized src type {config.SrcType}");
            }
        }

        private Tile Parse(string s)
        {
            if(s.Contains("!"))
            {
                // TODO: Cleanup and validate
                // TODO: Support hexagonal directions
                var a = s.Split('!');
                var b = a[1];
                var refl = false;
                if(b.StartsWith("x"))
                {
                    refl = true;
                    b = b.Substring(1);
                }
                var rotation = ((int.Parse(b) / 90) + 4) % 4;
                return new Tile(new RotatedTile
                {
                    Tile = Parse(a[0]),
                    ReflectX = refl,
                    RotateCw = rotation,
                });
            }
            // TODO: Apply tiles by name
            if (loader != null)
            {
                return loader.Parse(s);
            }
            else
            {
                return new Tile(s);
            }
        }

        private static TileModel GetModel(DeBroglieConfig config, Directions directions, ITopoArray<Tile>[] samples, TileRotation tileRotation)
        {
            var modelConfig = config.Model ?? new Adjacent();
            if (modelConfig is Overlapping overlapping)
            {
                var model = new OverlappingModel(overlapping.NX, overlapping.NY, overlapping.NZ);
                foreach (var sample in samples)
                {
                    model.AddSample(sample, config.RotationalSymmetry, config.ReflectionalSymmetry, tileRotation);
                }
                return model;
            }
            else if(modelConfig is Adjacent adjacent)
            {
                var model = new AdjacentModel(directions);
                foreach (var sample in samples)
                {
                    model.AddSample(sample, config.RotationalSymmetry, config.ReflectionalSymmetry, tileRotation);
                }
                return model;
            }
            throw new System.Exception($"Unrecognized model type {modelConfig.GetType()}");
        }

        private List<ITileConstraint> GetConstraints(bool is3d)
        {
            var constraints = new List<ITileConstraint>();
            if (config.Ground != null)
            {
                var groundTile = Parse(config.Ground);
                constraints.Add(new BorderConstraint
                {
                    Sides = is3d ? BorderSides.ZMin : BorderSides.YMax,
                    Tile = groundTile,
                });
                constraints.Add(new BorderConstraint
                {
                    Sides = is3d ? BorderSides.ZMin : BorderSides.YMax,
                    Tile = groundTile,
                    InvertArea = true,
                    Ban = true,
                });
            }

            if (config.Constraints != null)
            {
                foreach (var constraint in config.Constraints)
                {
                    if (constraint is PathConfig pathData)
                    {
                        var pathTiles = new HashSet<Tile>(pathData.PathTiles.Select(Parse));
                        var p = new PathConstraint(pathTiles);
                        constraints.Add(p);
                    }
                    else if (constraint is BorderConfig borderData)
                    {
                        var tile = Parse(borderData.Tile);
                        var sides = borderData.Sides == null ? BorderSides.All : (BorderSides)Enum.Parse(typeof(BorderSides), borderData.Sides, true);
                        var excludeSides = borderData.ExcludeSides == null ? BorderSides.None : (BorderSides)Enum.Parse(typeof(BorderSides), borderData.ExcludeSides, true);
                        if (!is3d)
                        {
                            sides = sides & ~BorderSides.ZMin & ~BorderSides.ZMax;
                            excludeSides = excludeSides & ~BorderSides.ZMin & ~BorderSides.ZMax;
                        }
                        constraints.Add(new BorderConstraint
                        {
                            Tile = tile,
                            Sides = sides,
                            ExcludeSides = excludeSides,
                            InvertArea = borderData.InvertArea,
                            Ban = borderData.Ban,
                        });
                    }else if (constraint is FixedTileConfig fixedTileConfig)
                    {
                        constraints.Add(new FixedTileConstraint
                        {
                            Tile = Parse(fixedTileConfig.Tile),
                            Point = fixedTileConfig.Point,
                        });
                    }
                }
            }

            return constraints;
        }

        public void ProcessItem()
        {
            if (config.Dest == null)
            {
                throw new System.Exception("dest attribute must be set");
            }

            var directory = config.BaseDirectory;

            var dest = Path.Combine(directory, config.Dest);
            var contdest = Path.ChangeExtension(dest, ".contradiction" + Path.GetExtension(dest));

            SampleSet sampleSet;
            if (config.SrcType == SrcType.Sample)
            {
                sampleSet = LoadSample();
            }
            else
            {
                sampleSet = LoadFileSet();
            }
            var directions = sampleSet.Directions;
            var samples = sampleSet.Samples;

            var is3d = directions.Type == DirectionsType.Cartesian3d;
            var topology = new Topology(directions, config.Width, config.Height, is3d ? config.Depth : 1, config.PeriodicX, config.PeriodicY, config.PeriodicZ);

            var tileRotation = GetTileRotation(config.Tiles, config.RotationTreatment, topology);

            var model = GetModel(config, directions, samples, tileRotation);

            // Setup adjacencies
            if(config.Adjacencies != null)
            {
                var adjacentModel = model as AdjacentModel;
                if(adjacentModel == null)
                {
                    throw new Exception("Setting adjacencies is only supported for the \"adjacent\" model.");
                }

                foreach(var a in config.Adjacencies)
                {
                    var srcAdj = a.Src.Select(Parse).Select(tileRotation.Canonicalize).ToList();
                    var destAdj = a.Dest.Select(Parse).Select(tileRotation.Canonicalize).ToList();
                    adjacentModel.AddAdjacency(srcAdj, destAdj, a.X, a.Y, a.Z, config.RotationalSymmetry, config.ReflectionalSymmetry, tileRotation);
                }

                // If there are no samples, set frequency to 1 for everything mentioned in this block
                foreach (var tile in adjacentModel.PatternsToTiles.Values)
                {
                    adjacentModel.SetFrequency(tile, 1);
                }
            }



            // Setup tiles
            if(config.Tiles != null)
            {
                foreach (var tile in config.Tiles)
                {
                    var value = Parse(tile.Value);
                    if(tile.MultiplyFrequency != null)
                    {
                        var cf = tile.MultiplyFrequency.Trim();
                        double cfd;
                        if(cf.EndsWith("%"))
                        {
                            cfd = double.Parse(cf.TrimEnd('%')) / 100;
                        }
                        else
                        {
                            cfd = double.Parse(cf);
                        }
                        model.MultiplyFrequency(value, cfd);
                    }
                }
            }

            // Setup constraints
            var constraints = GetConstraints(is3d);

            System.Console.WriteLine($"Processing {dest}");
            var propagator = new TilePropagator(model, topology, config.Backtrack, constraints: constraints.ToArray());

            Resolution status = propagator.Status;

            for (var retry = 0; retry < 5; retry++)
            {
                if (retry != 0)
                {
                    status = propagator.Clear();
                }
                if (status == Resolution.Contradiction)
                {
                    System.Console.WriteLine($"Found contradiction in initial conditions, retrying");
                    continue;
                }
                if (config.Animate)
                {
                    status = RunAnimate(model, propagator, dest, sampleSet.ExportOptions);
                }
                else
                {
                    status = propagator.Run();
                }
                if (status == Resolution.Contradiction)
                {
                    System.Console.WriteLine($"Found contradiction, retrying");
                    continue;
                }
                break;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (status == Resolution.Decided)
            {
                System.Console.WriteLine($"Writing {dest}");
                Exporter.Export(model, propagator, dest, config, sampleSet.ExportOptions);
                File.Delete(contdest);
            }
            else
            {
                System.Console.WriteLine($"Writing {contdest}");
                Exporter.Export(model, propagator, contdest, config, sampleSet.ExportOptions);
                File.Delete(dest);
            }
        }

        private Resolution RunAnimate(TileModel model, TilePropagator propagator, string dest, ExportOptions exportOptions)
        {
            if(!config.Animate)
            {
                return propagator.Run();
            }
            // Animate is true - we run the propagator, and export after every step
            Resolution status = Resolution.Undecided;
            var allFiles = new List<string>();
            int i = 0;
            while(true)
            {
                status = propagator.Step();
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                var currentDest = Path.ChangeExtension(dest, i + Path.GetExtension(dest));
                allFiles.Add(currentDest);
                Exporter.Export(model, propagator, currentDest, config, exportOptions);
                i++;
                if (status != Resolution.Undecided) return status;
            }
        }

        private TileRotation GetTileRotation(List<TileData> tileData, TileRotationTreatment? rotationTreatment, Topology topology)
        {

            var tileRotationBuilder = new TileRotationBuilder(rotationTreatment ?? TileRotationTreatment.Unchanged);

            // Setup tiles
            if (tileData != null)
            {
                foreach (var td in tileData)
                {
                    var tile = Parse(td.Value);
                    if(td.TileSymmetry != null)
                    {
                        var ts = TileSymmetryUtils.Parse(td.TileSymmetry);
                        tileRotationBuilder.AddSymmetry(tile, ts);
                    }
                    if (td.ReflectX != null)
                    {
                        tileRotationBuilder.Add(tile, 0, true, Parse(td.ReflectX));
                    }
                    if (td.ReflectY != null)
                    {
                        tileRotationBuilder.Add(tile, topology.Directions.Count / 2, true, Parse(td.ReflectY));
                    }
                    if (td.RotateCw != null)
                    {
                        tileRotationBuilder.Add(tile, 1, false, Parse(td.RotateCw));
                    }
                    if (td.RotateCcw != null)
                    {
                        tileRotationBuilder.Add(tile, -1, false, Parse(td.RotateCcw));
                    }
                    if(td.RotationTreatment != null)
                    {
                        tileRotationBuilder.SetTreatment(tile, td.RotationTreatment.Value);
                    }
                }
            }

            return tileRotationBuilder.Build();
        }

        private static DeBroglieConfig LoadItemsFile(string filename)
        {
            var serializer = new JsonSerializer();
            serializer.ContractResolver = new CamelCasePropertyNamesContractResolver();
            using (var fs = new FileStream(filename, FileMode.Open))
            using(var tr = new StreamReader(fs))
            using (var jsonReader = new JsonTextReader(tr))
            {
                var errors = new List<string>();

                serializer.Error += (object sender, Newtonsoft.Json.Serialization.ErrorEventArgs args) =>
                {
                    // only log an error once
                    if (args.CurrentObject == args.ErrorContext.OriginalObject)
                    {
                        errors.Add(args.ErrorContext.Error.Message);
                    }
                };
                var result = serializer.Deserialize<DeBroglieConfig>(jsonReader);
                if(errors.Count != 0)
                {
                    // TODO: Better diagnostics
                    throw new Exception(errors[0]);
                }
                if (result == null)
                    throw new Exception($"{filename} is empty.");
                return result;
            }
        }

        public static void Process(string filename)
        {
            var directory = Path.GetDirectoryName(filename);
            if (directory == "")
                directory = ".";
            var config = LoadItemsFile(filename);
            config.BaseDirectory = config.BaseDirectory == null ? directory : Path.Combine(directory, config.BaseDirectory);
            var importer = Importer.GetImporter(config.Src);

            var processor = new ItemsProcessor(importer, config);
            processor.ProcessItem();
        }
    }
}
