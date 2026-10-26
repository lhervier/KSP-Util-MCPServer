using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI.Screens;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Tools that read the inventories the player sees, slot by slot, with where each slot lies on screen, and the
    /// part held by the mouse: what a protocol needs to move parts between inventories with mouse_click.
    /// </summary>
    internal static class InventoryTools
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        // Private in ModuleInventoryPart: the volume and mass of the parts stored, which volumeCapacity and
        // massCapacity show, plus the part held while it previews a store.
        private static readonly FieldInfo VolumeOccupied = typeof(ModuleInventoryPart).GetField("volumeOccupied", Private);
        private static readonly FieldInfo MassOccupied = typeof(ModuleInventoryPart).GetField("massOccupied", Private);

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("read_inventories",
                "Reads the inventories shown, in flight and in the editors: those of the open part action windows " +
                "(where: partMenu), of the panel of EVA construction or cargo (constructionPanel) and of the " +
                "editors' inventory panel (editorPanel), with whether that panel is open, and collapsed: true for " +
                "one in a collapsed group of its window, whose slots take no click. For each: its part (id " +
                "as list_parts gives it, name, title, vessel), the kerbal for a kerbal's inventory, its volume and " +
                "mass (occupied: the parts stored; shown: what its footer shows, the held part included while it " +
                "previews a store; limit, 0 for none; reached: the flag that refuses a store), and its slots: " +
                "index, part name, variant, quantity, stack capacity, or empty, and the middle of each on screen " +
                "(x, y, pixels of the game's window from its top left corner, as mouse_click takes them) with " +
                "whether it lies within the visible part of its scrolled list. Also the part held by the mouse, " +
                "shared by every inventory: its name, stackSize and heldPartIsStack, the inventory it was taken " +
                "from, the slot clicked and the slot marked as emptied (each by where, part or kerbal and index, " +
                "with its quantity now), and the held icon with the amount it shows. An empty list when no " +
                "inventory is shown; held is null in a scene without the game's inventory controller.",
                Schema.Object(), ReadInventories) { Concurrent = true };
        }

        private static IEnumerator ReadInventories(ToolCall call)
        {
            List<UIPartActionInventory> shown = new List<UIPartActionInventory>();
            foreach (UIPartActionInventory inventory in Object.FindObjectsOfType<UIPartActionInventory>())
            {
                if (inventory != null && inventory.inventoryPartModule != null && inventory.gameObject.activeInHierarchy)
                {
                    shown.Add(inventory);
                }
            }
            // The windows first, then the panels; each group from the top of the screen.
            List<KeyValuePair<double, object>> sorted = new List<KeyValuePair<double, object>>();
            foreach (UIPartActionInventory inventory in shown)
            {
                double top = (double)StationTools.ScreenRect((RectTransform)inventory.transform)["top"];
                double group = inventory.inCargoPane ? 1e6 : 0;
                sorted.Add(new KeyValuePair<double, object>(group + top, Describe(inventory)));
            }
            sorted.Sort((a, b) => a.Key.CompareTo(b.Key));
            call.Text(new Dictionary<string, object>
            {
                { "inventories", sorted.ConvertAll(pair => pair.Value) },
                { "held", Held() }
            });
            yield break;
        }

        /// <summary>Describes one inventory shown: where, its owner, its volume and mass, and its slots.</summary>
        private static Dictionary<string, object> Describe(UIPartActionInventory inventory)
        {
            ModuleInventoryPart module = inventory.inventoryPartModule;
            Dictionary<string, object> described = Owner(inventory);
            described["slotCount"] = module.InventorySlots;
            described["volume"] = Limit(VolumeOccupied, module, module.volumeCapacity, module.packedVolumeLimit,
                module.volumeCapacityReached);
            described["mass"] = Limit(MassOccupied, module, module.massCapacity, module.massLimit,
                module.massCapacityReached);
            // The panels keep their inventories while they slide out of the screen.
            if (inventory.inCargoPane)
            {
                described["panelOpen"] = HighLogic.LoadedSceneIsEditor
                    ? InventoryPanelController.Instance != null && InventoryPanelController.Instance.IsOpen
                    : EVAConstructionModeController.Instance != null && EVAConstructionModeController.Instance.IsOpen;
            }
            // The grid of slots is hidden while the astronaut complex shows over the editor.
            described["gridShown"] = inventory.gridLayout == null || inventory.gridLayout.gameObject.activeInHierarchy;
            // A collapsed group of a part action window keeps its content active, but transparent and deaf to clicks.
            if (BlockedByCanvasGroup(inventory.transform))
            {
                described["collapsed"] = true;
            }
            List<object> slots = new List<object>();
            foreach (UIPartActionInventorySlot slot in inventory.slotButton)
            {
                if (slot == null)
                {
                    continue;
                }
                Dictionary<string, object> place = StationTools.RowPlace((RectTransform)slot.transform);
                Dictionary<string, object> item = new Dictionary<string, object> { { "slot", slot.slotIndex } };
                StoredPart stored;
                if (module.storedParts != null && module.storedParts.TryGetValue(slot.slotIndex, out stored) && stored != null)
                {
                    item["part"] = stored.partName;
                    if (!string.IsNullOrEmpty(stored.variantName))
                    {
                        item["variant"] = stored.variantName;
                    }
                    item["quantity"] = stored.quantity;
                    item["stackCapacity"] = stored.stackCapacity;
                }
                else
                {
                    item["empty"] = true;
                }
                item["x"] = place["x"];
                item["y"] = place["y"];
                item["inView"] = place["inView"];
                if (!slot.gameObject.activeInHierarchy)
                {
                    item["shown"] = false;
                }
                slots.Add(item);
            }
            described["slots"] = slots;
            return described;
        }

        /// <summary>
        /// Whether a canvas group around <paramref name="transform"/> makes it invisible or lets no click reach it.
        /// </summary>
        private static bool BlockedByCanvasGroup(Transform transform)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                foreach (CanvasGroup group in current.GetComponents<CanvasGroup>())
                {
                    if (!group.enabled)
                    {
                        continue;
                    }
                    if (group.alpha <= 0f || !group.blocksRaycasts)
                    {
                        return true;
                    }
                    // Such a group overrides the groups above it.
                    if (group.ignoreParentGroups)
                    {
                        return false;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Where an inventory is shown (partMenu, constructionPanel or editorPanel), and whose it is: its part (id,
        /// name, title, vessel; null for a kerbal's inventory kept aboard, which has no part) and, for a kerbal's
        /// inventory, the kerbal's name.
        /// </summary>
        private static Dictionary<string, object> Owner(UIPartActionInventory inventory)
        {
            ModuleInventoryPart module = inventory.inventoryPartModule;
            Dictionary<string, object> owner = new Dictionary<string, object> { { "where", Where(inventory) } };
            Part part = module.part;
            if (part != null && part.partInfo != null)
            {
                owner["part"] = new Dictionary<string, object>
                {
                    { "id", (double)part.persistentId },
                    { "name", part.partInfo.name },
                    { "title", part.partInfo.title },
                    { "vessel", part.vessel != null ? part.vessel.GetDisplayName() : null }
                };
            }
            else
            {
                owner["part"] = null;
            }
            if (module.kerbalMode)
            {
                owner["kerbal"] = module.kerbalReference.name;
            }
            return owner;
        }

        /// <summary>Where an inventory is shown: partMenu, constructionPanel or editorPanel.</summary>
        private static string Where(UIPartActionInventory inventory)
        {
            // Both panels set their inventories up for the cargo pane; only the scene tells them apart.
            if (!inventory.inCargoPane)
            {
                return "partMenu";
            }
            return HighLogic.LoadedSceneIsEditor ? "editorPanel" : "constructionPanel";
        }

        /// <summary>
        /// A limit of an inventory: what its parts occupy (read from the module's private field, null when the
        /// field is not found), what its footer shows, the limit (0 for none) and the flag that refuses a store.
        /// </summary>
        private static Dictionary<string, object> Limit(FieldInfo occupied, ModuleInventoryPart module, float shown,
            float limit, bool reached)
        {
            return new Dictionary<string, object>
            {
                { "occupied", occupied != null ? (object)(double)(float)occupied.GetValue(module) : null },
                { "shown", (double)shown },
                { "limit", (double)limit },
                { "reached", reached }
            };
        }

        /// <summary>
        /// The part held by the mouse and the state UIPartActionControllerInventory keeps for it; null when that
        /// controller does not exist.
        /// </summary>
        private static Dictionary<string, object> Held()
        {
            UIPartActionControllerInventory controller = UIPartActionControllerInventory.Instance;
            if (controller == null)
            {
                return null;
            }
            Part part = controller.CurrentCargoPart;
            Dictionary<string, object> held = new Dictionary<string, object>
            {
                { "part", part != null && part.partInfo != null ? part.partInfo.name : null },
                { "stackSize", UIPartActionControllerInventory.stackSize },
                { "heldPartIsStack", UIPartActionControllerInventory.heldPartIsStack },
                { "amountToStore", UIPartActionControllerInventory.amountToStore },
                { "from", controller.CurrentInventory != null ? ModuleRef(controller.CurrentInventory) : null },
                { "slotClicked", SlotRef(controller.CurrentInventorySlotClicked) },
                { "slotEmptied", SlotRef(controller.CurrentInventorySlotEmptied) }
            };
            EditorInventoryOnlyIcon icon = controller.CurrentInventoryOnlyIcon;
            if (icon != null)
            {
                bool amountShown = icon.StackAmountText != null && icon.StackAmountText.gameObject.activeSelf;
                held["icon"] = new Dictionary<string, object>
                {
                    { "shown", icon.gameObject.activeInHierarchy },
                    { "amount", amountShown ? icon.StackAmountText.text : null }
                };
            }
            return held;
        }

        /// <summary>
        /// A slot of an inventory shown, by where, part or kerbal and index, with the quantity it holds now; null for
        /// none, and destroyed: true alone for a slot whose window is gone, which the game reads as none.
        /// </summary>
        private static Dictionary<string, object> SlotRef(UIPartActionInventorySlot slot)
        {
            if (ReferenceEquals(slot, null))
            {
                return null;
            }
            if (slot == null || slot.inventoryPartActionUI == null || slot.inventoryPartActionUI.inventoryPartModule == null)
            {
                return new Dictionary<string, object> { { "destroyed", true } };
            }
            ModuleInventoryPart module = slot.inventoryPartActionUI.inventoryPartModule;
            Dictionary<string, object> slotRef = ModuleRef(module);
            slotRef["where"] = Where(slot.inventoryPartActionUI);
            slotRef["slot"] = slot.slotIndex;
            slotRef["quantity"] = module.GetStackAmountAtSlot(slot.slotIndex);
            return slotRef;
        }

        /// <summary>An inventory by its part's id, or by its kerbal's name for a kerbal's inventory, or both.</summary>
        private static Dictionary<string, object> ModuleRef(ModuleInventoryPart module)
        {
            Dictionary<string, object> moduleRef = new Dictionary<string, object>();
            if (module.part != null)
            {
                moduleRef["part"] = (double)module.part.persistentId;
            }
            if (module.kerbalMode)
            {
                moduleRef["kerbal"] = module.kerbalReference.name;
            }
            return moduleRef;
        }
    }
}
