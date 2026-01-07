using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Fdp.Kernel;
using ImGuiNET;
using Fdp.Examples.CarKinem.Input;

namespace Fdp.Examples.CarKinem.UI
{
    /// <summary>
    /// Interactive entity inspector for debugging using ImGui.
    /// Shows entities in 3 panels: Top (entity list), Middle (component list), Bottom (component details).
    /// Generic implementation for any component types.
    /// </summary>
    public class EntityInspector
    {
        private EntityRepository _repo;
        private SelectionManager _selectionManager;
        
        // Local cache of entities for display
        private List<Entity> _entities = new();
        private List<ComponentInfo> _components = new();
        private int _selectedComponentIndex = 0;
        
        // Reflection cache
        private static readonly Dictionary<Type, MethodInfo> _hasComponentMethods = new();
        private static readonly Dictionary<Type, MethodInfo> _getComponentMethods = new();

        public EntityInspector()
        {
        }

        public void SetContext(EntityRepository repo, SelectionManager selectionManager)
        {
            _repo = repo;
            _selectionManager = selectionManager;
        }
        
        public void Update()
        {
            if (_repo == null) return;

            // Refresh entity list
            _entities.Clear();
            var index = _repo.GetEntityIndex();
            // Iterate all issued indices
            for (int i = 0; i <= index.MaxIssuedIndex; i++)
            {
                ref var header = ref index.GetHeader(i);
                if (header.IsActive)
                {
                    _entities.Add(new Entity(i, header.Generation));
                }
            }
            
            // Sync selection from SelectionManager
            int selectedIndex = -1;
            if (_selectionManager?.SelectedEntityId != null)
            {
                selectedIndex = _entities.FindIndex(e => e.Index == _selectionManager.SelectedEntityId.Value);
            }
            
            // Refresh component list for selected entity
            if (selectedIndex >= 0 && selectedIndex < _entities.Count)
            {
                var selectedEntity = _entities[selectedIndex];
                _components = GetComponentsForEntity(selectedEntity);
                
                if (_selectedComponentIndex >= _components.Count)
                    _selectedComponentIndex = Math.Max(0, _components.Count - 1);
            }
            else
            {
                _components.Clear();
            }
        }
        
        public void DrawImGui()
        {
            // Position the inspector window on the right side
            ImGui.SetNextWindowPos(new Vector2(1920 - 510, 10), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(500, 1060), ImGuiCond.FirstUseEver);
            
            if (!ImGui.Begin("Entity Inspector", ImGuiWindowFlags.NoCollapse))
            {
                ImGui.End();
                return;
            }

            // TOP PANEL: Entity List
            DrawEntityListPanel();
            
            ImGui.Separator();
            
            // MIDDLE PANEL: Component List
            DrawComponentListPanel();
            
            ImGui.Separator();
            
            // BOTTOM PANEL: Component Details
            DrawComponentDetailsPanel();

            ImGui.End();
        }
        
        private void DrawEntityListPanel()
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1), $"Entities ({_entities.Count})");
            ImGui.BeginChild("EntityListChild", new Vector2(0, 300));
            
            if (ImGui.BeginTable("EntityTable", 3,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("Gen", ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn("Summary", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                
                for (int i = 0; i < _entities.Count; i++)
                {
                    var entity = _entities[i];
                    bool isSelected = (_selectionManager?.SelectedEntityId == entity.Index);
                    
                    ImGui.TableNextRow();
                    
                    if (isSelected)
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.5f, 1)));
                    
                    ImGui.TableSetColumnIndex(0);
                    if (ImGui.Selectable($"{entity.Index}", isSelected, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        if (_selectionManager != null)
                            _selectionManager.SelectedEntityId = entity.Index;
                    }
                    
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text($"{entity.Generation}");
                    
                    ImGui.TableSetColumnIndex(2);
                    // Generic summary: Try to find "Name" or "Type" component or just Position
                    ImGui.Text(GetGenericEntitySummary(entity));
                }
                
                ImGui.EndTable();
            }
            
            ImGui.EndChild();
        }
        
        private void DrawComponentListPanel()
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1), $"Components ({_components.Count})");
            ImGui.BeginChild("ComponentListChild", new Vector2(0, 250));
            
            if (_selectionManager?.SelectedEntityId == null)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No entity selected");
            }
            else if (ImGui.BeginTable("ComponentTable", 2,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Component", ImGuiTableColumnFlags.WidthFixed, 150);
                ImGui.TableSetupColumn("Summary", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                
                for (int i = 0; i < _components.Count; i++)
                {
                    var comp = _components[i];
                    bool isSelected = (i == _selectedComponentIndex);
                    
                    ImGui.TableNextRow();
                    
                    if (isSelected)
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.5f, 1)));
                    
                    ImGui.TableSetColumnIndex(0);
                    if (ImGui.Selectable(comp.TypeName, isSelected, ImGuiSelectableFlags.SpanAllColumns))
                        _selectedComponentIndex = i;
                    
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(comp.Summary);
                }
                
                ImGui.EndTable();
            }
            
            ImGui.EndChild();
        }
        
        private void DrawComponentDetailsPanel()
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "Component Details");
            ImGui.BeginChild("ComponentDetailsChild", new Vector2(0, 0));
            
            if (_components.Count == 0 || _selectedComponentIndex >= _components.Count)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No component selected");
            }
            else
            {
                var comp = _components[_selectedComponentIndex];
                
                ImGui.TextColored(new Vector4(0, 1, 1, 1), comp.TypeName);
                ImGui.Separator();
                
                if (ImGui.BeginTable("PropertiesTable", 2,
                    ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg))
                {
                    ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthFixed, 120);
                    ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableHeadersRow();
                    
                    foreach (var prop in comp.Properties)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextColored(new Vector4(0, 1, 1, 1), prop.Key);
                        ImGui.TableSetColumnIndex(1);
                        ImGui.Text(prop.Value);
                    }
                    
                    ImGui.EndTable();
                }
            }
            
            ImGui.EndChild();
        }

        private string GetGenericEntitySummary(Entity entity)
        {
            // Try to find UnitStats or Position to show something useful
            // We use the string matching for types to be safe
            var summary = "";
            
            // This is "Generic" but we prioritize known helpful types for summary
            // But checking ALL components via GetAllTypes() here would be too slow for the list
            // So we rely on a few common ones or just say "Active"
            
            // To be truly generic but efficient we might skip complex checks here
            return "Active";
        }
        
        private List<ComponentInfo> GetComponentsForEntity(Entity entity)
        {
            var result = new List<ComponentInfo>();
            var allTypes = ComponentTypeRegistry.GetAllTypes();
            
            foreach (var type in allTypes)
            {
                if (HasComponent(_repo, entity, type))
                {
                    var comp = GetComponent(_repo, entity, type);
                    if (comp != null)
                    {
                        result.Add(new ComponentInfo
                        {
                            TypeName = type.Name,
                            Summary = GetGenericObjectSummary(comp),
                            Properties = GetGenericProperties(comp)
                        });
                    }
                }
            }
            
            return result;
        }

        private string GetGenericObjectSummary(object obj)
        {
            if (obj == null) return "null";
            if (obj is Vector2 v2) return $"({v2.X:F1}, {v2.Y:F1})";
            if (obj is Vector3 v3) return $"({v3.X:F1}, {v3.Y:F1}, {v3.Z:F1})";
            
            // Try to find a "Name" property or "Type" property
             var type = obj.GetType();
             var nameProp = type.GetProperty("Name") ?? type.GetProperty("Type");
             if (nameProp != null)
             {
                 return nameProp.GetValue(obj)?.ToString() ?? "";
             }
             
             // Or fields
             var nameField = type.GetField("Name") ?? type.GetField("Type");
             if (nameField != null)
             {
                 return nameField.GetValue(obj)?.ToString() ?? "";
             }

             return obj.ToString();
        }

        private Dictionary<string, string> GetGenericProperties(object obj)
        {
            var props = new Dictionary<string, string>();
            if (obj == null) return props;
            
            var type = obj.GetType();
            
            // Fields
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                props[field.Name] = FormatValue(field.GetValue(obj));
            }
            
            // Properties
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length == 0) // Skip indexers
                {
                    try {
                        props[prop.Name] = FormatValue(prop.GetValue(obj));
                    } catch {
                        props[prop.Name] = "<error>";
                    }
                }
            }
            
            return props;
        }
        
        private string FormatValue(object value)
        {
            if (value == null) return "null";
            if (value is float f) return f.ToString("F3");
            if (value is double d) return d.ToString("F3");
            if (value is Vector2 v2) return $"({v2.X:F2}, {v2.Y:F2})";
            return value.ToString();
        }

        // Generic Component Access
        private bool HasComponent(EntityRepository repository, Entity entity, Type type)
        {
            if (!_hasComponentMethods.TryGetValue(type, out var method))
            {
                method = typeof(EntityRepository).GetMethod("HasComponent", new[] { typeof(Entity) })
                                                 ?.MakeGenericMethod(type);
                _hasComponentMethods[type] = method;
            }

            if (method == null) return false;
            return (bool)method.Invoke(repository, new object[] { entity });
        }

        private object GetComponent(EntityRepository repository, Entity entity, Type type)
        {
            if (!_getComponentMethods.TryGetValue(type, out var method))
            {
                method = typeof(EntityRepository).GetMethod("GetComponentRO", new[] { typeof(Entity) })
                                                 ?.MakeGenericMethod(type);
                _getComponentMethods[type] = method;
            }

            if (method == null) return null;
            return method.Invoke(repository, new object[] { entity });
        }

        private class ComponentInfo
        {
            public string TypeName { get; set; } = "";
            public string Summary { get; set; } = "";
            public Dictionary<string, string> Properties { get; set; } = new();
        }
    }
}
