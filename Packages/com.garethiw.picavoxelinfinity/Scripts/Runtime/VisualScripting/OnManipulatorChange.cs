using System.Collections;
using System.Collections.Generic;
using PicaVoxel;
using UnityEngine;
using Unity.VisualScripting;

namespace PicaVoxel
{
    [UnitTitle("On Voxel Manipulator Change")]
    [UnitShortTitle("On Voxel Manipulator Change")]
    [UnitCategory("Events\\PicaVoxel")]
    public class OnVoxelManipulatorChange : EventUnit<VoxelChangeEventArgs>
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

            result = ValueOutput<VoxelChangeEventArgs>("Change");
        }

        protected override bool ShouldTrigger(Flow flow, VoxelChangeEventArgs data)
        {
            return true;
        }

        // Setting the value on our port.
        protected override void AssignArguments(Flow flow, VoxelChangeEventArgs data)
        {
            flow.SetValue(result, data);
        }
    }
}
