using System.Collections;
using System.Collections.Generic;
using PicaVoxel;
using UnityEngine;
using Unity.VisualScripting;

namespace PicaVoxelInfinity.VisualScripting
{
    [UnitTitle("On Voxel Manipulator Change")]
    [UnitShortTitle("On Voxel Manipulator Change")]
    [UnitCategory("Events\\PicaVoxel")]
    public class OnVoxelManipulatorChange : EventUnit<VoxelManipulatorChangeEventArgs>
    {
        [DoNotSerialize]
        public ValueOutput result;

        protected override bool register => true;

        public override EventHook GetHook(GraphReference reference)
        {
            return new EventHook("OnVoxelManipulatorChange");
        }

        protected override void Definition()
        {
            base.Definition();

            result = ValueOutput<string>("Data");
        }

        protected override bool ShouldTrigger(Flow flow, VoxelManipulatorChangeEventArgs data)
        {
            return true;
        }

        // Setting the value on our port.
        protected override void AssignArguments(Flow flow, VoxelManipulatorChangeEventArgs data)
        {
            flow.SetValue(result, data);
        }
    }
}
