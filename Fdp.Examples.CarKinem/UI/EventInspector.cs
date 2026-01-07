using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Linq;
using Fdp.Kernel;
using ImGuiNET;

namespace Fdp.Examples.CarKinem.UI
{
    /// <summary>
    /// Event inspector for debugging event bus activity using ImGui.
    /// Shows both unmanaged and managed events from current and previous frames.
    /// Generic implementation that supports all event types.
    /// </summary>
    public class EventInspector
    {
        private FdpEventBus _eventBus;
        
        // Event history tracking
        private List<EventRecord> _currentFrameEvents = new();
        private List<EventRecord> _previousFrameEvents = new();
        private EventRecord? _selectedEvent = null;

        
        public void SetEventBus(FdpEventBus bus)
        {
            if (_eventBus != bus)
            {
                _eventBus = bus;
                _currentFrameEvents.Clear();
                _previousFrameEvents.Clear();
                _selectedEvent = null;
            }
        }
        
        public void Update()
        {
            if (_eventBus == null) return;

            // Move current to previous
            _previousFrameEvents = _currentFrameEvents;
            _currentFrameEvents = new List<EventRecord>();
            
            // Iterate all inspectors
            foreach (var inspector in _eventBus.GetDebugInspectors())
            {
                if (inspector.Count == 0) continue;

                bool isManaged = !inspector.EventType.IsValueType;
                
                foreach (var evt in inspector.InspectReadBuffer())
                {
                    _currentFrameEvents.Add(new EventRecord
                    {
                        TypeName = inspector.EventType.Name + (isManaged ? " (Managed)" : ""),
                        IsManaged = isManaged,
                        Summary = GetGenericEventSummary(evt),
                        Details = GetGenericEventDetails(evt, inspector.EventType)
                    });
                }
            }
        }
        
        public void DrawImGui()
        {
            // Position the inspector window on the right side, below entity inspector
            ImGui.SetNextWindowPos(new Vector2(10, 600), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(600, 470), ImGuiCond.FirstUseEver);
            
            if (!ImGui.Begin("Event Inspector", ImGuiWindowFlags.NoCollapse))
            {
                ImGui.End();
                return;
            }

            // Stats header
            ImGui.TextColored(new Vector4(1, 1, 0, 1), 
                $"Events - Current: {_currentFrameEvents.Count}, Previous: {_previousFrameEvents.Count}");
            ImGui.Separator();
            
            // Two-panel layout: Event list on left, details on right
            if (ImGui.BeginTable("EventInspectorLayout", 2, ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Events", ImGuiTableColumnFlags.WidthFixed, 300);
                ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch);
                
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                DrawEventListPanel();
                
                ImGui.TableSetColumnIndex(1);
                DrawEventDetailsPanel();
                
                ImGui.EndTable();
            }

            ImGui.End();
        }
        
        private void DrawEventListPanel()
        {
            ImGui.TextColored(new Vector4(0, 1, 1, 1), "Current Frame");
            ImGui.BeginChild("CurrentFrameEvents", new Vector2(0, 180));
            
            if (_currentFrameEvents.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No events this frame");
            }
            else
            {
                for (int i = 0; i < _currentFrameEvents.Count; i++)
                {
                    var evt = _currentFrameEvents[i];
                    bool isSelected = (evt == _selectedEvent);
                    
                    var color = evt.IsManaged 
                        ? new Vector4(0.5f, 1f, 0.5f, 1f)  // Green for managed
                        : new Vector4(1f, 1f, 1f, 1f);      // White for unmanaged
                    
                    if (isSelected)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 0f, 1f));
                    else
                        ImGui.PushStyleColor(ImGuiCol.Text, color);
                    
                    if (ImGui.Selectable($"{evt.TypeName}##current{i}", isSelected))
                        _selectedEvent = evt;
                    
                    ImGui.PopStyleColor();
                    
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(evt.Summary);
                }
            }
            
            ImGui.EndChild();
            
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Previous Frame");
            ImGui.BeginChild("PreviousFrameEvents", new Vector2(0, 180));
            
            if (_previousFrameEvents.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No events");
            }
            else
            {
                for (int i = 0; i < _previousFrameEvents.Count; i++)
                {
                    var evt = _previousFrameEvents[i];
                    bool isSelected = (evt == _selectedEvent);
                    
                    var color = evt.IsManaged 
                        ? new Vector4(0.4f, 0.7f, 0.4f, 1f)  // Dimmed green
                        : new Vector4(0.7f, 0.7f, 0.7f, 1f); // Dimmed white
                    
                    if (isSelected)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 0f, 1f));
                    else
                        ImGui.PushStyleColor(ImGuiCol.Text, color);
                    
                    if (ImGui.Selectable($"{evt.TypeName}##prev{i}", isSelected))
                        _selectedEvent = evt;
                        
                    ImGui.PopStyleColor();
                    
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(evt.Summary);
                }
            }
            
            ImGui.EndChild();
        }
        
        private void DrawEventDetailsPanel()
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "Event Details");
            ImGui.BeginChild("EventDetails", new Vector2(0, 0));
            
            if (_selectedEvent == null)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No event selected");
            }
            else
            {
                var evt = _selectedEvent;
                
                ImGui.TextColored(new Vector4(0, 1, 1, 1), evt.TypeName);
                ImGui.Separator();
                
                ImGui.TextWrapped(evt.Summary);
                ImGui.Spacing();
                ImGui.Separator();
                
                if (ImGui.BeginTable("EventDetailsTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                {
                    ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthFixed, 120);
                    ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableHeadersRow();
                    
                    foreach (var detail in evt.Details)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextColored(new Vector4(0, 1, 1, 1), detail.Key);
                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextWrapped(detail.Value);
                    }
                    
                    ImGui.EndTable();
                }
            }
            
            ImGui.EndChild();
        }
        
        private string GetGenericEventSummary(object evt)
        {
            if (evt == null) return "null";
            
            var type = evt.GetType();
            // Try to find reasonable summary properties
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(string) || p.PropertyType.IsEnum)
                .Take(3)
                .Select(p => $"{p.Name}: {p.GetValue(evt)}")
                .ToList();
                
            if (props.Count == 0 && type.IsValueType)
            {
                 var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(f => f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.IsEnum)
                    .Take(3)
                    .Select(f => $"{f.Name}: {f.GetValue(evt)}")
                    .ToList();
                 props.AddRange(fields);
            }

            if (props.Count > 0)
                return string.Join(", ", props);
                
            return evt.ToString();
        }
        
        private Dictionary<string, string> GetGenericEventDetails(object evt, Type type)
        {
            var details = new Dictionary<string, string>();
            if (evt == null) return details;
            
            // Unmanaged events use Fields (structs)
            if (type.IsValueType)
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    var value = field.GetValue(evt);
                    details[field.Name] = FormatValue(value);
                }
            }
            // Managed events use Properties (classes)
            else
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        // Skip IgnoreMember properties for cleaner view
                        if (prop.GetCustomAttributes(true).Any(a => a.GetType().Name == "IgnoreMemberAttribute"))
                            continue;
                        var value = prop.GetValue(evt);
                        details[prop.Name] = FormatValue(value);
                    }
                    catch
                    {
                        details[prop.Name] = "<error>";
                    }
                }
            }
            
            return details;
        }

        private string FormatValue(object value)
        {
            if (value == null) return "null";
            if (value is Vector2 v2) return $"({v2.X:F2}, {v2.Y:F2})";
            if (value is Vector3 v3) return $"({v3.X:F2}, {v3.Y:F2}, {v3.Z:F2})";
            if (value is float f) return f.ToString("F2");
            if (value is double d) return d.ToString("F2");
            return value.ToString();
        }
        
        private class EventRecord
        {
            public string TypeName { get; set; } = "";
            public bool IsManaged { get; set; }
            public string Summary { get; set; } = "";
            public Dictionary<string, string> Details { get; set; } = new();
        }
    }
}
