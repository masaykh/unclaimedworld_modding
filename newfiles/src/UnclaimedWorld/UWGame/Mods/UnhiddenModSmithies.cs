using System.Collections.Generic;
using Microsoft.Xna.Framework;
using UWGame.Client.Particles;
using UWGame.ClientSide.Renderables;
using UWGame.SimSide.Buildings;
using UWGame.SimSide.Collisions;
using UWGame.SimSide.Entities;
using UWGame.SimSide.Entities.Containers;
using UWGame.SimSide.Entities.Containers.Components;
using UWGame.SimSide.Policies;
using UWGame.SimSide.XmlCollections;

namespace UWGame.Mods;

/// <summary>
/// The Unhidden Mod's smithy redefinitions - see <see cref="UnhiddenMod"/> for provenance and
/// for the switch that gates them.
///
/// Both smithies are replaced wholesale rather than edited, because that is how the original
/// Harmony patch expressed it: it could not reach into a constructed EntityType, so it rebuilt
/// the entry and swapped it into the list. Kept that way here so this stays diffable against
/// patches/NewItemsAndRecipes.cs.
///
/// The substantive change in both is one field. The studio pins forge fuel to a specific item:
///
///     RequiresFuelType { FuelTypeKeyName = "item:charcoal" }
///
/// and the mod pins it to a tag instead:
///
///     RequiresFuelType { FuelTypeTag = "fuelForForge" }
///
/// which is what lets peat charcoal be burned in a forge at all. Everything else here - the
/// descriptions, sprites, particle emitters, collision shapes, part lists - is carried over
/// unchanged so the two entries stay consistent with the rest of the table.
/// </summary>
internal static class UnhiddenModSmithies
{
    public static void Replace(List<EntityType> listOfEntityTypes)
    {
        ReplaceSimpleSmithy(listOfEntityTypes);
        ReplaceImprovisedSmithy(listOfEntityTypes);
    }

    private static void ReplaceSimpleSmithy(List<EntityType> listOfEntityTypes)
    {
        int index = listOfEntityTypes.FindIndex((EntityType s) => s.KeyName == "structure:simpleSmithy");
        if (index < 0)
        {
            return;
        }

        listOfEntityTypes[index] = new EntityType("structure:simpleSmithy")
        {
            Name = "Smithy (simple)",
            SummaryDescription = "Simple furnace for smelting / heating iron and an iron anvil. Fuel: charcoal",
            Description = "A step up from the improvised smithy with its rock anvil, this smithy equipped with an iron anvil can make more sophisticated iron objects. \n \nThe primitive bloomery furnace is built from clay. It requires charcoal as fuel and an air supply tool such as a bellows. After smelting iron ore in the furnace, a solid iron bloom is worked on the anvil with a hammer, removing slag until low-carbon 'wrought iron' is produced. This can be further worked into iron tools.",
            ThumbnailSmall = "HUD_thumbnail_forgeSimple",
            CategoryKey = "production",
            StructureType = new StructureType
            {
                BuildByPlayer = true
            },
            ToolType = new ToolType
            {
                ToolTag = new string[1] { "furnace" },
                Durability = 0.9f,
                ToolHandling = ToolHandlingType.Stationary,
                PrepareProcess = "forgeSmoke"
            },
            TierOrArea = new TierOrArea
            {
                Tier = "basic"
            },
            RenderableType = new RenderableType
            {
                DefaultClientState = new ClientStateInfo
                {
                    RenderAsBillboardType = new RenderAsBillboardType[1]
                    {
                        new RenderAsBillboardType
                        {
                            AssetName = "forgeSimple"
                        }
                    },
                    RenderAsGroundSpriteType = new RenderAsGroundSpriteType
                    {
                        AssetName = "forgeSimple_g"
                    }
                },
                ClientStateConditions = new ClientStateInfo[3]
                {
                    new ClientStateInfo
                    {
                        RenderAsBillboardType = new RenderAsBillboardType[1]
                        {
                            new RenderAsBillboardType
                            {
                                AssetName = "forgeSimple"
                            }
                        },
                        Conditions = new BitMask64(typeof(StateModifier), 0)
                    },
                    new ClientStateInfo
                    {
                        RenderAsGroundSpriteType = new RenderAsGroundSpriteType
                        {
                            AssetName = "forgeSimple_g"
                        },
                        Conditions = new BitMask64(typeof(StateModifier), 1)
                    },
                    new ClientStateInfo
                    {
                        RenderAsBillboardType = new RenderAsBillboardType[1]
                        {
                            new RenderAsBillboardType
                            {
                                AssetName = "forgeSimple"
                            }
                        },
                        RenderAsGroundSpriteType = new RenderAsGroundSpriteType
                        {
                            AssetName = "forgeSimple_g"
                        },
                        Conditions = new BitMask64(typeof(StateModifier), 39),
                        ParticleEmitters = new ParticleEmitterEffect[2]
                        {
                            new ParticleEmitterEffect
                            {
                                ParticleSystemKey = "smallestSmoke",
                                Offset = new Vector2(-11f, -22f)
                            },
                            new ParticleEmitterEffect
                            {
                                ParticleSystemKey = "tinyFire",
                                Offset = new Vector2(-19f, 4f)
                            }
                        }
                    }
                }
            },
            ContainerType = new WorkshopContainerType
            {
                CanTransactWithTags = new string[1] { "humanTransact" },
                ItemStorageType = new ItemStorageType("isolated", 1f, null, null, null, null),
                StorageTags = new string[2] { "storageTagLiquidContainerClosedNoHeat", "storageTagLiquidContainerNoHeat" },
                DefaultStorageSettings = "forgeStorage",
                RequiresReplenishType = new RequiresReplenishType
                {
                    ReplenishProcess = "refuelSmithy",
                    RequiresFuelType = new RequiresFuelType
                    {
                        MaxFuel = 1f,
                        // The mod's actual change: a fuel TAG rather than FuelTypeKeyName =
                        // "item:charcoal", so any "fuelForForge" item qualifies.
                        FuelTypeTag = "fuelForForge",
                        BurnRatePerDay = 2f
                    }
                }
            },
            DefaultSimState = new SimStateInfo
            {
                GeometryLayoutType = new GeometryLayoutType
                {
                    Pad = 8f,
                    PadShape = CollidePrim.Circle,
                    Shapes = new CollideShape2D[2]
                    {
                        new CollideShape2D(new Vector2(-9f, -4f), 16f),
                        new CollideShape2D(new Vector2(17f, 2f), 14f)
                    }
                }
            },
            NonLivingType = new NonLivingType
            {
                PartsAreWeatherProof = true,
                DegradeType = "sturdyConstruction",
                SalvageProcess = "salvageSimpleSmithy",
                Repair = "buildingRepair",
                PartKeys = new SerializableDictionary<string, int>
                {
                    { "item:solidMudBrick", 3 },
                    { "item:anvil", 1 },
                    { "item:barClamps", 1 }
                }
            }
        };
    }

    private static void ReplaceImprovisedSmithy(List<EntityType> listOfEntityTypes)
    {
        int index = listOfEntityTypes.FindIndex((EntityType s) => s.KeyName == "structure:improvisedSmithy");
        if (index < 0)
        {
            return;
        }

        listOfEntityTypes[index] = new EntityType("structure:improvisedSmithy")
        {
            Name = "Smithy (improvised)",
            SummaryDescription = "Simple furnace for smelting / heating iron and a rock anvil. Fuel:charcoal",
            Description = "The primitive furnace is built from clay. To reach a sufficient temperature, it requires charcoal as fuel and an air supply tool such as a bellows. After smelting the ore in the furnace, the metal is shaped on the anvil with a hammer.",
            ThumbnailSmall = "HUD_thumbnail_forgeImprovised",
            CategoryKey = "production",
            StructureType = new StructureType
            {
                BuildByPlayer = true
            },
            ToolType = new ToolType
            {
                ToolTag = new string[1] { "furnace" },
                Durability = 0.9f,
                ToolHandling = ToolHandlingType.Stationary,
                PrepareProcess = "forgeSmoke"
            },
            TierOrArea = new TierOrArea
            {
                Tier = "basic"
            },
            RenderableType = new RenderableType
            {
                DefaultClientState = new ClientStateInfo
                {
                    RenderAsBillboardType = new RenderAsBillboardType[1]
                    {
                        new RenderAsBillboardType
                        {
                            AssetName = "forgeImprovised"
                        }
                    },
                    RenderAsGroundSpriteType = new RenderAsGroundSpriteType
                    {
                        AssetName = "forgeImprovised_g"
                    }
                },
                ClientStateConditions = new ClientStateInfo[3]
                {
                    new ClientStateInfo
                    {
                        RenderAsBillboardType = new RenderAsBillboardType[1]
                        {
                            new RenderAsBillboardType
                            {
                                AssetName = "forgeImprovised"
                            }
                        },
                        Conditions = new BitMask64(typeof(StateModifier), 0)
                    },
                    new ClientStateInfo
                    {
                        RenderAsGroundSpriteType = new RenderAsGroundSpriteType
                        {
                            AssetName = "forgeImprovised_g"
                        },
                        Conditions = new BitMask64(typeof(StateModifier), 1)
                    },
                    new ClientStateInfo
                    {
                        RenderAsBillboardType = new RenderAsBillboardType[1]
                        {
                            new RenderAsBillboardType
                            {
                                AssetName = "forgeImprovised"
                            }
                        },
                        RenderAsGroundSpriteType = new RenderAsGroundSpriteType
                        {
                            AssetName = "forgeImprovised_g"
                        },
                        Conditions = new BitMask64(typeof(StateModifier), 39),
                        ParticleEmitters = new ParticleEmitterEffect[2]
                        {
                            new ParticleEmitterEffect
                            {
                                ParticleSystemKey = "smallestSmoke",
                                Offset = new Vector2(-9f, -26f)
                            },
                            new ParticleEmitterEffect
                            {
                                ParticleSystemKey = "tinyFire",
                                Offset = new Vector2(-15f, 0f)
                            }
                        }
                    }
                }
            },
            ContainerType = new WorkshopContainerType
            {
                CanTransactWithTags = new string[1] { "humanTransact" },
                ItemStorageType = new ItemStorageType("isolated", 1f, null, null, null, null),
                StorageTags = new string[2] { "storageTagLiquidContainerClosedNoHeat", "storageTagLiquidContainerNoHeat" },
                DefaultStorageSettings = "forgeStorage",
                RequiresReplenishType = new RequiresReplenishType
                {
                    ReplenishProcess = "refuelSmithy",
                    RequiresFuelType = new RequiresFuelType
                    {
                        MaxFuel = 1f,
                        FuelTypeTag = "fuelForForge",
                        BurnRatePerDay = 2f
                    }
                }
            },
            DefaultSimState = new SimStateInfo
            {
                GeometryLayoutType = new GeometryLayoutType
                {
                    Pad = 10f,
                    PadShape = CollidePrim.Circle,
                    Shapes = new CollideShape2D[2]
                    {
                        new CollideShape2D(new Vector2(-9f, -8f), 14f),
                        new CollideShape2D(new Vector2(17f, 2f), 14f)
                    }
                }
            },
            NonLivingType = new NonLivingType
            {
                PartsAreWeatherProof = true,
                DegradeType = "sturdyConstruction",
                SalvageProcess = "salvageImprovisedSmithy",
                Repair = "buildingRepair",
                PartKeys = new SerializableDictionary<string, int>
                {
                    { "item:solidMudBrick", 3 },
                    { "item:stones", 1 }
                }
            }
        };
    }
}
