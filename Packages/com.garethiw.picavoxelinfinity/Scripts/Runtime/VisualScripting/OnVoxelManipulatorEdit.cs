using System.Collections;
using System.Collections.Generic;
using PicaVoxel;
using UnityEngine;
using Unity.VisualScripting;

namespace PicaVoxel
{
    [UnitTitle("On Voxel Manipulator Edit")]
    [UnitShortTitle("On Voxel Manipulator Edit")]
    [UnitCategory("Events\\PicaVoxel")]
    public class OnVoxelManipulatorEdit : EventUnit<VoxelEditEventArgs>
    {
        [DoNotSerialize]
        public ValueOutput result;

        protected override bool register => true;

        public override EventHook GetHook(GraphReference reference)
        {
            return new EventHook("OnVoxelManipulatorEdit");
        }

        protected override void Definition()
        {
            base.Definition();

            result = ValueOutput<VoxelEditEventArgs>("VoxelEdited");
        }

        protected override bool ShouldTrigger(Flow flow, VoxelEditEventArgs data)
        {
            return true;
        }

        // Setting the value on our port.
        protected override void AssignArguments(Flow flow, VoxelEditEventArgs data)
        {
            flow.SetValue(result, data);
        }
    }
}
