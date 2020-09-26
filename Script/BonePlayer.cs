using System.Collections.Generic;
using System.Linq;
using Array = System.Array;
using UnityEngine;
using Unity.Collections;
using AsyncGPUReadbackRequest = UnityEngine.Rendering.AsyncGPUReadbackRequest;

namespace ShaderMotion {
public class BonePlayer {
	Skeleton skeleton;
	MotionLayout layout;
	public SkinnedMeshRenderer shapeRenderer = null;

	public Vector3    hipsT;
	public Quaternion hipsQ;
	public float      hipsS;
	public Vector3[]  boneSwingTwists;
	public Dictionary<string, float> shapes;

	public BonePlayer(Skeleton skeleton, MotionLayout layout) {
		this.skeleton = skeleton;
		this.layout = layout;

		boneSwingTwists = new Vector3[skeleton.bones.Length];
		shapes = new Dictionary<string, float>();
	}

	const float PositionScale = 2;
	const int textureWidth = 40, textureHeight = 45;
	private NativeArray<Color> texture = new NativeArray<Color>();
	float SampleSlot(int idx) {
		int x = idx / textureHeight;
		int y = idx % textureHeight;
		return texture[x + (textureHeight-1-y) * textureWidth].r;
	}	
	public void Update(AsyncGPUReadbackRequest req) {
		texture = req.GetData<Color>();

		var hipsTT = new float[6];
		var hipsY = Vector3.up;
		var hipsZ = Vector3.forward;
		Array.Clear(boneSwingTwists, 0, boneSwingTwists.Length);
		for(int i=0; i<skeleton.bones.Length; i++) {
			var slot = layout.baseIndices[i];
			foreach(var j in layout.channels[i]) {
				var v = SampleSlot(slot);
				if(j<3)
					boneSwingTwists[i][j] = v * 180;
				else if(j<9)
					hipsTT[j-3] = v;
				else if(j<12)
					hipsY[j-9] = v;
				else if(j<15)
					hipsZ[j-12] = v;

				slot++;
			}
		}
		for(int i=0; i<3; i++)
			hipsT[i] = Util.DecodeVideoFloat(hipsTT[i], hipsTT[i+3]);
		(hipsY, hipsZ) = Util.conformalize(hipsY, hipsZ);
		hipsT *= PositionScale;
		hipsY *= PositionScale;
		hipsZ *= PositionScale;
		hipsQ = Quaternion.LookRotation(hipsZ, hipsY);
		hipsS = hipsY.magnitude;

		shapes.Clear();
		foreach(var si in layout.shapeIndices) {
			float w;
			shapes.TryGetValue(si.shape, out w);
			shapes[si.shape] = w + SampleSlot(si.index) * si.weight;
		}
	}
	private HumanPoseHandler poseHandler;
	private HumanPose pose;
	public void ApplyHumanPose() {
		if(poseHandler == null) {
			poseHandler = new HumanPoseHandler(skeleton.root.GetComponent<Animator>().avatar, skeleton.root);
			poseHandler.GetHumanPose(ref pose);
		}
		CalcHumanPose(out pose.bodyPosition, out pose.bodyRotation, pose.muscles);
		poseHandler.SetHumanPose(ref pose);
	}
	public void ApplyTransform() {
		for(int i=0; i<skeleton.bones.Length; i++)
			if(skeleton.bones[i]) {
				var axes = skeleton.axes[i];
				if(i != (int)HumanBodyBones.Hips)
					skeleton.bones[i].localRotation = axes.preQ * Util.fromSwingTwist(axes.sign * boneSwingTwists[i])
												* Quaternion.Inverse(axes.postQ);
				else {
					var rescale = hipsS / skeleton.scale;
					skeleton.root.localScale = new Vector3(1,1,1) * rescale;
					skeleton.bones[i].SetPositionAndRotation(
									skeleton.root.TransformPoint(hipsT / rescale),
									skeleton.root.rotation * hipsQ * Quaternion.Inverse(axes.postQ));
				}
			}
	}
	public void ApplyBlendShape() {
		if(shapeRenderer) {
			var mesh = shapeRenderer.sharedMesh;
			foreach(var kv in shapes) {
				var shape = mesh.GetBlendShapeIndex(kv.Key);
				var frame = mesh.GetBlendShapeFrameCount(shape)-1;
				var weight = mesh.GetBlendShapeFrameWeight(shape, frame);
				shapeRenderer.SetBlendShapeWeight(shape, kv.Value * weight);
			}
		}
	}

	static readonly (int, Vector3)[] spreadRootQ = new[]{
		((int)HumanBodyBones.Spine, new Vector3(21.04f, -29.166f, -29.166f)),
		((int)HumanBodyBones.Chest, new Vector3(21.04f, -18.517f, -18.517f)),
	};
	void CalcHumanPose(out Vector3 rootT, out Quaternion rootQ, float[] muscles) {
		Array.Clear(muscles, 0, muscles.Length);
		for(int i=0; i<HumanTrait.BoneCount; i++)
			for(int j=0; j<3; j++) {
				var (muscle, weight) = boneMuscles[i, j];
				if(muscle >= 0)
					muscles[muscle] += boneSwingTwists[i][j] * weight;
			}
		for(int i=0; i<HumanTrait.MuscleCount; i++)
			muscles[i] /= muscles[i] >= 0 ? muscleLimits[i,1] : -muscleLimits[i,0];
		
		rootT = hipsT / hipsS;
		rootQ = hipsQ;
		foreach(var (i, scale) in spreadRootQ) { 
			var axes = skeleton.axes[i];
			var r = Util.fromSwingTwist(axes.sign * new Vector3(
				muscles[boneMuscles[i,0].Item1]*scale[0],
				muscles[boneMuscles[i,1].Item1]*scale[1],
				muscles[boneMuscles[i,2].Item1]*scale[2]));
			var t = Quaternion.LookRotation(Vector3.right, Vector3.forward);
			rootQ *= t * r * Quaternion.Inverse(t);
		}
	}

	public static readonly (int, float)[,] boneMuscles;
	public static readonly float[,] muscleLimits;
	static BonePlayer() {
		boneMuscles = new (int, float)[HumanTrait.BoneCount, 3];
		for(int i=0; i<HumanTrait.BoneCount; i++) 
			for(int j=0; j<3; j++) {
				var ii = i;
				var jj = j;
				var muscle = HumanTrait.MuscleFromBone(ii, jj);
				var weight = (float)1;
				if(muscle < 0) {
					switch(ii) {
					case (int)HumanBodyBones.LeftShoulder:
						ii = (int)HumanBodyBones.LeftUpperArm; break;
					case (int)HumanBodyBones.RightShoulder:
						ii = (int)HumanBodyBones.RightUpperArm; break;
					case (int)HumanBodyBones.Jaw:
						break;
					case (int)HumanBodyBones.LeftLowerArm:
					case (int)HumanBodyBones.RightLowerArm:
						weight = -1;
						jj = 0;
						goto default;
					case (int)HumanBodyBones.LeftLowerLeg:
					case (int)HumanBodyBones.RightLowerLeg:
						jj = 0;
						goto default;
					default:
						ii = HumanTrait.GetParentBone(ii);break;
					}
					muscle = HumanTrait.MuscleFromBone(ii, jj);
				}
				boneMuscles[i, j] = (muscle, weight);
			}
		muscleLimits = new float[HumanTrait.MuscleCount, 2];
		for(int i=0; i<HumanTrait.MuscleCount; i++) {
			muscleLimits[i, 0] = HumanTrait.GetMuscleDefaultMin(i);
			muscleLimits[i, 1] = HumanTrait.GetMuscleDefaultMax(i);
		}
	}
}
}