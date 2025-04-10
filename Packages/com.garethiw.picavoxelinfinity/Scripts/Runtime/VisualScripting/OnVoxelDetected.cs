using System.Collections;
using System.Collections.Generic;
using PicaVoxel;
using UnityEngine;
using Unity.VisualScripting;

namespace PicaVoxel
{
    [UnitTitle("On Voxel Detected")]
    [UnitShortTitle("On Voxel Detected")]
    [UnitCategory("Events\\PicaVoxel")]
    public class OnVoxelDetected : EventUnit<VoxelDetectorEventArgs>
    {
        [DoNotSerialize]
        public ValueOutput result;

        protected override bool register => true;

        public override EventHook GetHook(GraphReference reference)
        {
            return new EventHook("OnVoxelDetected");
        }

        protected override void Definition()
        {
            base.Definition();

            result = ValueOutput<VoxelDetectorEventArgs>("VoxelDetectorEventArgs");
        }

        protected override bool ShouldTrigger(Flow flow, VoxelDetectorEventArgs data)
        {
            return true;
        }

        // Setting the value on our port.
        protected override void AssignArguments(Flow flow, VoxelDetectorEventArgs data)
        {
            flow.SetValue(result, data);
        }
    }
}
