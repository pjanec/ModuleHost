using System;
using System.Collections.Generic;
using System.Numerics;
using CarKinem.Core;

namespace CarKinem.Formation
{
    /// <summary>
    /// Singleton manager for formation templates.
    /// </summary>
    public class FormationTemplateManager : IDisposable
    {
        private readonly Dictionary<FormationType, FormationTemplate> _templates = new();
        
        public FormationTemplateManager()
        {
            RegisterDefaultTemplates();
        }
        
        private void RegisterDefaultTemplates()
        {
            // Column formation (single file)
            var column = new FormationTemplate
            {
                Type = FormationType.Column,
                SlotOffsets = new Vector2[16]
            };
            
            for (int i = 0; i < 16; i++)
            {
                // Offset behind leader (negative X = behind)
                column.SlotOffsets[i] = new Vector2(-(i + 1) * 5.0f, 0f);
            }
            _templates[FormationType.Column] = column;
            
            // Wedge formation (V-shape)
            var wedge = new FormationTemplate
            {
                Type = FormationType.Wedge,
                SlotOffsets = new Vector2[16]
            };
            
            for (int i = 0; i < 16; i++)
            {
                int row = (i / 2) + 1;
                int side = (i % 2 == 0) ? 1 : -1;
                wedge.SlotOffsets[i] = new Vector2(-row * 4.0f, side * row * 3.0f);
            }
            _templates[FormationType.Wedge] = wedge;
            
            // Line formation (horizontal line)
            var line = new FormationTemplate
            {
                Type = FormationType.Line,
                SlotOffsets = new Vector2[16]
            };
            
            for (int i = 0; i < 16; i++)
            {
                int side = (i % 2 == 0) ? 1 : -1;
                int offset = (i / 2) + 1;
                line.SlotOffsets[i] = new Vector2(0f, side * offset * 4.0f);
            }
            _templates[FormationType.Line] = line;
        }
        
        public FormationTemplate GetTemplate(FormationType type)
        {
            return _templates.TryGetValue(type, out var template) ? template : _templates[FormationType.Column];
        }
        
        public void RegisterCustomTemplate(FormationType type, Vector2[] slotOffsets)
        {
            _templates[type] = new FormationTemplate
            {
                Type = type,
                SlotOffsets = slotOffsets
            };
        }
        
        public void Dispose()
        {
            _templates.Clear();
        }
    }
}
