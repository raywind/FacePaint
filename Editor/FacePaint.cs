﻿using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Sigtrap.FacePaint {
	public class FacePaint : EditorWindow {
		#region Static
		[MenuItem("Window/FacePaint")]
		public static void Open(){
			// Get existing open window or if none, make a new one:
			FacePaint window = (FacePaint)EditorWindow.GetWindow(typeof(FacePaint));
			window.Show();
		}
		#endregion

		#region Plugin API
		#region Color info
		/// <summary>
		/// Color selected in main FacePaint GUI
		/// </summary>
		/// <value>The color of the paint.</value>
		public Color paintColor {get {return _c;}}
		/// <summary>
		/// Is painting to RED channel enabled?
		/// </summary>
		public bool writeR {get {return _mR;}}
		/// <summary>
		/// Is painting to GREEN channel enabled?
		/// </summary>
		public bool writeG {get {return _mG;}}
		/// <summary>
		/// Is painting to BLUE channel enabled?
		/// </summary>
		public bool writeB {get {return _mB;}}
		/// <summary>
		/// Is painting to ALPHA channel enabled?
		/// </summary>
		public bool writeA {get {return _mA;}}
		#endregion

		#region GUI helpers
		/// <summary>
		/// Draw a button with a background color
		/// </summary>
		/// <returns><c>true</c>, if button pressed, <c>false</c> otherwise.</returns>
		/// <param name="label">Label.</param>
		/// <param name="bCol">Button color.</param>
		public bool DrawBtn(string label, Color bCol){
			Color gbc =	GUI.backgroundColor;
			GUI.backgroundColor = bCol;
			bool result = GUILayout.Button(label);
			GUI.backgroundColor = gbc;
			return result;
		}

		/// <summary>
		/// Draw a button with a background color and text color
		/// </summary>
		/// <returns><c>true</c>, if button pressed, <c>false</c> otherwise.</returns>
		/// <param name="label">Label.</param>
		/// <param name="bCol">Button color.</param>
		/// <param name="tCol">Text color.</param>
		public bool DrawBtn(string label, Color bCol, Color tCol){
			Color gcc =	GUI.contentColor;
			GUI.contentColor = tCol;
			bool result = DrawBtn(label, bCol);
			GUI.contentColor = gcc;
			return result;
		}
		#endregion

		#region Core methods
		/// <summary>
		/// Paint over existing color, respecting channel settings
		/// </summary>
		/// <param name="baseCol">Color to paint over</param>
		/// <param name="paintCol">Paint color</param>
		public Color Paint(Color baseCol, Color paintCol){
			if (_mR) {
				baseCol.r = paintCol.r;
			}
			if (_mG) {
				baseCol.g = paintCol.g;
			}
			if (_mB) {
				baseCol.b = paintCol.b;
			}
			if (_mA) {
				baseCol.a = paintCol.a;
			}
			return baseCol;
		}
		#endregion
		#endregion

		#region Edit data
		private GameObject _go;
		private MeshFilter _mf;

		private bool _editing {
			get {
				return (_go != null && _mf != null);
			}
		}
		#endregion

		#region Color settings
		private bool _paintIsland = false;
		private Color _defaultColor;
		private Color _c;

		bool[] _mask = new bool[]{ true, true, true, true };

		bool _mR { get { return _mask[0]; } set { _mask[0] = value; } }
		bool _mG { get { return _mask[1]; } set { _mask[1] = value; } }
		bool _mB { get { return _mask[2]; } set { _mask[2] = value; } }
		bool _mA { get { return _mask[3]; } set { _mask[3] = value; } }

		int _channels {
			get {
				int c = 0;
				for (int i = 0; i < 4; ++i) {
					if (_mask[i]) ++c;
				}
				return c;
			}
		}

		float _activeChannel {
			get {
				if (_channels == 1) {
					if (_mR) return _c.r;
					if (_mG) return _c.g;
					if (_mB) return _c.b;
					if (_mA) return _c.a;
				}
				return -1;
			}
			set {
				if (_mR) _c.r = value;
				if (_mG) _c.g = value;
				if (_mB) _c.b = value;
				if (_mA) _c.a = value;
			}
		}

		bool _debug = false;
		bool _wasDebug = false;
		Material __debugMat;
		Material _debugMat {
			get {
				if (__debugMat == null){
					__debugMat = new Material(Shader.Find("Hidden/FacePaintDebug"));
				}
				return __debugMat;
			}
		}
		Material[] _origMats = null;
		int __debugMask = 0;
		int _debugMask {
			get {return __debugMask;}
			set {
				__debugMask = value;
				_debugMat.SetInt("_Mask", __debugMask);
			}
		}
		#endregion

		#region UI settings
		Color _hlCol = Color.green;
		int _hlThick = 5;
		bool _hl = true;

		Color _btnCol = new Color(0.7f, 1f, 0.7f);
		Vector2 _scroll = new Vector2();
		Texture _bucketIcon;

		bool _showPlugins = false;
		bool _showSettings = true;
		#endregion

		#region Plugins
		List<IFacePaintPlugin> _plugins = new List<IFacePaintPlugin>();
		List<bool> _pluginsActive = new List<bool>();
		int _numPluginsActive {
			get {
				if (_plugins.Count == 0) return 0;
				int result = 0;
				for (int i=0; i<_pluginsActive.Count; ++i){
					if (_pluginsActive[i]){
						++result;
					}
				}
				return result;
			}
		}
		bool _anyPluginsActive {get {return _numPluginsActive > 0;}}
		bool _anyPluginsHoverTris {
			get {
				if (!_anyPluginsActive) return false;
				for (int i=0; i<_plugins.Count; ++i){
					if (_plugins[i].forceTriangleHover){
						return true;
					}
				}
				return false;
			}
		}
		#endregion

		#region Subscription
		void OnEnable(){
			SceneView.onSceneGUIDelegate += OnSceneGUI;
			EditorApplication.update += EditorUpdate;
			Undo.undoRedoPerformed += OnUndoRedo;

			_bucketIcon = Resources.Load("paint-can-icon") as Texture;
			titleContent = new GUIContent("FacePaint");

			// Get plugins
			_plugins.Clear();
			_pluginsActive.Clear();
			System.Type iPlugin = typeof(IFacePaintPlugin);
			foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies()){
				foreach (var t in a.GetTypes()){
					if (t.IsPublic && !t.IsAbstract && !t.IsInterface && t.GetInterfaces().Contains(iPlugin)){
						_plugins.Add((IFacePaintPlugin)System.Activator.CreateInstance(t));
						_pluginsActive.Add(false);
					}
				}
			}
		}
		void OnDisable(){
			SceneView.onSceneGUIDelegate -= OnSceneGUI;
			EditorApplication.update -= EditorUpdate;
			Undo.undoRedoPerformed -= OnUndoRedo;

			if (__debugMat != null){
				DestroyImmediate(__debugMat);
			}
		}
		#endregion

		#region Editor/GUI helper methods
		// Most of these are a bit more stateful than they should be, but oh well...

		void EditorUpdate(){
			if (Selection.activeGameObject != null) {
				CheckSelection();
			}
			// Force GUI updates even when not focused
			Repaint();
		}

		/// <summary>
		/// Automatically finish editing if user selects another object
		/// </summary>
		void CheckSelection(){
			if (!_editing) return;
			if (Selection.activeGameObject != _go) {
				Done();
			}
		}

		/// <summary>
		/// Finish editing and reset all temporary stuff
		/// </summary>
		void Done(){
			if (!_editing) return;
			if (_debug) {
				DisableDebug();
			}
			FacePaintData fpd = _mf.GetComponent<FacePaintData>();
			if (fpd) {
				fpd.Apply();
			}
			_go = null;
			_mf = null;
		}

		void EnableDebug(){
			if (!_editing) return;

			_debug = true;
			MeshRenderer mr = _mf.GetComponent<MeshRenderer>();
			// Store 'real' materials
			_origMats = mr.sharedMaterials;
			// Assign new materials with debug shader
			Material[] newMats = new Material[_origMats.Length];
			for (int i = 0; i < newMats.Length; ++i) {
				newMats[i] = _debugMat;
			}
			mr.sharedMaterials = newMats;
			// Make sure shader settings are set
			_debugMask = _debugMask;
		}

		void DisableDebug(){
			if (_origMats == null) return;

			_debug = false;
			MeshRenderer mr = _mf.GetComponent<MeshRenderer>();
			// Restore 'real' materials
			mr.sharedMaterials = _origMats;
			_origMats = null;
		}
		#endregion


		#region Data
		FacePaintData GetColorData(MeshFilter mf){
			FacePaintData cd = mf.GetComponent<FacePaintData>();
			if (cd == null) {
				cd = Undo.AddComponent<FacePaintData>(mf.gameObject);
				cd.Init(_defaultColor);
			}
			return cd;
		}

		void OnUndoRedo(){
			if (_mf) {
				FacePaintData fpd = _mf.GetComponent<FacePaintData>();
				if (fpd) {
					fpd.Apply();
				}
			}
		}
		#endregion

		#region Main
		void OnGUI(){
			Color gc = GUI.color;
			Color gcc = GUI.contentColor;
			CheckSelection();
			_scroll = EditorGUILayout.BeginScrollView(_scroll);

			if (Selection.activeGameObject == null) {
				EditorGUILayout.HelpBox("No Object Selected",MessageType.Info);
				EditorGUILayout.Space();
			} else {

				MeshFilter tmf = Selection.activeGameObject.GetComponentInChildren<MeshFilter>();

				EditorGUILayout.Space();
				if (!_editing) {
					if (!tmf) {
						GUI.contentColor = Color.red;
					}
					EditorGUILayout.HelpBox("Selected: " + Selection.activeGameObject.name, MessageType.None);
					GUI.contentColor = gcc;

					if (tmf) {
						MeshFilter[] mfs = Selection.activeGameObject.GetComponentsInChildren<MeshFilter>();
						string blPrefix = "";
						if (mfs.Length > 1) {
							EditorGUILayout.LabelField("Edit: ");
						} else {
							EditorGUILayout.Space();
							blPrefix = "Edit ";
						}
						foreach (MeshFilter m in mfs) {
							string bl = blPrefix;
							if (Selection.activeGameObject != m.gameObject) {
								bl += m.gameObject.name + "  >  ";
							}
							bl += m.sharedMesh.name;
								
							if (DrawBtn(bl, _btnCol)) {
								_go = Selection.activeGameObject = m.gameObject;
								_mf = m;
							}
						}
					}
				} else if (_editing) {
					#region Header
					if (Selection.activeGameObject != _mf.gameObject) {
						EditorGUILayout.HelpBox("Editing: "
						+ Selection.activeGameObject.name + " > "
						+ " > " + _mf.gameObject.name
						+ _mf.sharedMesh.name, MessageType.None);
					} else {
						EditorGUILayout.HelpBox("Editing: "
						+ Selection.activeGameObject.name + " > "
						+ _mf.sharedMesh.name, MessageType.None);
					}
					FacePaintData fpd = GetColorData(_mf);
					if (_paintIsland && !fpd.islandsMapped){
						EditorGUILayout.HelpBox("WARNING! Islands have not been calculated. Using ISLAND paint mode the first time may be very slow.",
							MessageType.Warning);
					}
					EditorGUILayout.Space();

					Undo.RecordObject(fpd, "FacePaint GUI");

					if (DrawBtn("DONE", _btnCol)) {
						Done();
					}
					#endregion

					#region Basic settings
					EditorGUILayout.Space();
					EditorGUILayout.Space();
					if (_channels != 0) {
						EditorGUILayout.BeginHorizontal();

						if (_channels == 1) {
							_activeChannel = EditorGUILayout.Slider("Value ", _activeChannel, 0f, 1f);
						} else {
							_c = EditorGUILayout.ColorField("Colour", _c);
						}

						if (GUILayout.Button(_bucketIcon)) {
							Color[] cols = fpd.GetColors();
							for (int i = 0; i < _mf.sharedMesh.vertexCount; ++i) {
								cols[i] = Paint(cols[i], _c);
							}
							fpd.SetColors(cols);
						}
						EditorGUILayout.EndHorizontal();
					}
					EditorGUILayout.BeginHorizontal();
					GUILayout.Label("Channels");
					if (DrawBtn("R", _mR ? Color.red : Color.white, Color.white)) _mR = !_mR;
					if (DrawBtn("G", _mG ? Color.green : Color.white, Color.white)) _mG = !_mG;
					if (DrawBtn("B", _mB ? new Color(0.5f,0.5f,1) : Color.white, Color.white)) _mB = !_mB;
					if (DrawBtn("A", _mA ? Color.gray : Color.white, Color.white)) _mA = !_mA;
					EditorGUILayout.EndHorizontal();

					EditorGUILayout.BeginHorizontal();
					GUILayout.Label("");
					if (GUILayout.Button("[Colour]")) {
						_mR = _mG = _mB = true;
						_mA = false;
					}
					if (GUILayout.Button("[Alpha]")) {
						_mR = _mG = _mB = false;
						_mA = true;
					}
					if (GUILayout.Button("[All]")) {
						_mR = _mG = _mB = _mA = true;
					}
					EditorGUILayout.EndHorizontal();

					_paintIsland = EditorGUILayout.Toggle(new GUIContent("Fill Island", "Clicking a face will also paint all connected faces"), _paintIsland);
					#endregion

					#region Plugins
					if (_plugins.Count > 0){
						EditorGUILayout.Space();
						EditorGUILayout.Space();
						string pLabel = string.Format(
							"PLUGINS [Active: {0} / {1}]",
							_numPluginsActive.ToString(),
							_plugins.Count
						);
						_showPlugins = EditorGUILayout.Foldout(_showPlugins, pLabel);
						if (_showPlugins){
							++EditorGUI.indentLevel;
							FacePaintGUIData data = new FacePaintGUIData();
							for (int i=0; i<_plugins.Count; ++i){
								IFacePaintPlugin fpp = _plugins[i];
								_pluginsActive[i] = EditorGUILayout.ToggleLeft(fpp.title, _pluginsActive[i]);
								if (_pluginsActive[i]){
									++EditorGUI.indentLevel;
									fpp.OnGUI(this, fpd, data);
									--EditorGUI.indentLevel;
								}
							}
							--EditorGUI.indentLevel;
						}
						EditorGUILayout.Space();
						EditorGUILayout.Space();
					}
					#endregion

					#region Debug shader
					_debug = EditorGUILayout.Toggle("View Vertex Colours", _debug);
					if (_debug && !_wasDebug) {
						EnableDebug();
					} else if (!_debug && _wasDebug) {
						DisableDebug();
					}
					if (_debug) {
						EditorGUILayout.BeginHorizontal();
						GUILayout.Label("");
						GUILayout.Label("Show:");
						GUI.enabled = (_debugMask != 0);
						if (DrawBtn("RGB", GUI.enabled ? Color.white : Color.grey)) _debugMask = 0;

						GUI.enabled = (_debugMask != 1);
						if (DrawBtn("R", GUI.enabled ? Color.white : Color.red)) _debugMask = 1;

						GUI.enabled = (_debugMask != 2);
						if (DrawBtn("G", GUI.enabled ? Color.white : Color.green)) _debugMask = 2;

						GUI.enabled = (_debugMask != 3);
						if (DrawBtn("B", GUI.enabled ? Color.white : Color.blue)) _debugMask = 3;

						GUI.enabled = (_debugMask != 4);
						if (DrawBtn("A", GUI.enabled ? Color.white : Color.black, Color.white)) _debugMask = 4;

						GUI.enabled = true;

						EditorGUILayout.EndHorizontal();
					}
					_wasDebug = _debug;
					#endregion
				}
			}

			#region Persistent settings
			EditorGUILayout.Space();
			_showSettings = EditorGUILayout.Foldout(_showSettings, "SETTINGS");
			if (_showSettings){
				++EditorGUI.indentLevel;
				GUIContent dctt = new GUIContent("Default Color", "When edited the very first time, meshes are filled with this color");
				_defaultColor = EditorGUILayout.ColorField(dctt, _defaultColor);

				_hl = EditorGUILayout.Toggle("Highlight faces", _hl);
				if (_hl) {
					++EditorGUI.indentLevel;
					_hlCol = EditorGUILayout.ColorField("Poly Highlight Colour", _hlCol);
					_hlThick = (int)EditorGUILayout.Slider("Thickness", (float)_hlThick, 5, 20);
					--EditorGUI.indentLevel;
				}

				if (_hl){
					EditorGUILayout.HelpBox("Highlighting may be slow on large meshes", MessageType.Warning);
				}
				--EditorGUI.indentLevel;
			}
			#endregion

			GUI.color = gc;
			GUI.contentColor = gcc;
			EditorGUILayout.EndScrollView();
		}

		bool _mouseWasDown = false;
		public void OnSceneGUI(SceneView sceneView){
			if (Selection.activeGameObject == null) return;
			CheckSelection();
			if (!_editing) return;
			Event e = Event.current;
			if (e.type == EventType.Used || e.type == EventType.used) return;

			if (e.modifiers == EventModifiers.None) {
				// Setup raycast from mouse position
				Ray mRay = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
				RaycastHit hit;

				MeshRenderer mr = _mf.GetComponent<MeshRenderer>();
				if (mr == null) {
					Debug.LogWarning("FacePaint: No MeshRenderer found on " + _mf.name);
					return;
				}

				// Check renderer bounds for cheap initial raycast
				if (mr.bounds.IntersectRay(mRay)) {
					bool clicked = e.button == 0 && (e.type == EventType.MouseDrag || e.type == EventType.MouseDown);
					// Tell Unity to ignore LMB click event to avoid selecting another object
					if (e.type == EventType.Layout){
						HandleUtility.AddDefaultControl(GUIUtility.GetControlID(GetHashCode(), FocusType.Passive));
					}

					#region Plugins MouseUp hook
					if (_anyPluginsActive && _mouseWasDown && !clicked){
						// Pass mouseUp event to plugins
						// Get existing mesh data
						FacePaintData f = GetColorData(_mf);
						Undo.RecordObject(f, "FacePaint SceneGUI");
						FacePaintSceneGUIData data = new FacePaintSceneGUIData(
							FacePaintSceneGUIData.SceneGUIEvent.M_UP
						);
						for (int i=0; i<_plugins.Count; ++i){
							if (_pluginsActive[i]){
								_plugins[i].OnSceneGUI(this, f, data);
							}
						}
					}
					#endregion

					_mouseWasDown = clicked;
					bool hoverTris = _anyPluginsHoverTris;
					if (!_hl && !clicked && !hoverTris) return;

					// Grab meshcollider or create temporary one
					bool newCollider = false;
					MeshCollider mc = mr.GetComponent<MeshCollider>();
					if (mc == null) {
						newCollider = true;
						mc = mr.gameObject.AddComponent<MeshCollider>();
					}

					// Get existing mesh data
					FacePaintData fpd = GetColorData(_mf);
					Undo.RecordObject(fpd, "FacePaint SceneGUI");

					// Use mesh collider to get exact triangle hit
					if (mc.Raycast(mRay, out hit, 100f)) {
						Mesh m = _mf.sharedMesh;
						int[] tris = m.triangles;
						Color[] cols = fpd.GetColors();
						int i0 = hit.triangleIndex * 3;

						if (clicked){
							// If clicked on a triangle, paint
							Event.current.Use();
							List<int> allTris;
							if (_paintIsland){
								allTris = fpd.GetConnectedTriangles(hit.triangleIndex);
							} else {
								allTris = new List<int>{hit.triangleIndex};
							}
							for (int i = 0; i < allTris.Count; ++i){
								for (int j = 0; j < 3; ++j) {
									int cInt = tris[(allTris[i]*3) + j];
									cols[cInt] = Paint(cols[cInt], _c);
								}
							}
							fpd.SetColors(cols);

							#region Plugin hook
							// Pass click/drag events to plugins
							if (_anyPluginsActive){
								FacePaintSceneGUIData.SceneGUIEvent sge = FacePaintSceneGUIData.SceneGUIEvent.M_DOWN;
								if (e.type == EventType.MouseDrag){
									sge = FacePaintSceneGUIData.SceneGUIEvent.M_DRAG;
								}
								FacePaintSceneGUIData data = new FacePaintSceneGUIData(
									sge, hit.triangleIndex,
									tris[i0], tris[i0+1], tris[i0+2]
								);
								for (int i=0; i<_plugins.Count; ++i){
									if (_pluginsActive[i]){
										_plugins[i].OnSceneGUI(this, fpd, data);
									}
								}
							}
							#endregion
						} else if (hoverTris) {
							// If not clicked/dragging, pass triangle hover event to plugins
							FacePaintSceneGUIData data = new FacePaintSceneGUIData(
								FacePaintSceneGUIData.SceneGUIEvent.HOVER_TRIS,
								hit.triangleIndex,
								tris[i0], tris[i0+1], tris[i0+2]
							);
							for (int i=0; i<_plugins.Count; ++i){
								if (_pluginsActive[i]){
									_plugins[i].OnSceneGUI(this, fpd, data);
								}
							}
						}
						if (_hl) {
							// Highlight hovered triangle
							Matrix4x4 hm = Handles.matrix;
							Handles.matrix = _mf.transform.localToWorldMatrix;
							Vector3[] verts = m.vertices;
							Color hc = Handles.color;
							Handles.color = _hlCol;
							Handles.DrawAAPolyLine(
								_hlThick, 
								verts[tris[i0]], verts[tris[i0 + 1]],
								verts[tris[i0 + 2]], verts[tris[i0]]);
							Handles.matrix = hm;
							Handles.color = hc;
						}
					} else {
						#region Plugin hook
						if (_anyPluginsActive){
							FacePaintSceneGUIData data = new FacePaintSceneGUIData(
								FacePaintSceneGUIData.SceneGUIEvent.HOVER_MESH
							);
							for (int i=0; i<_plugins.Count; ++i){
								if (_pluginsActive[i]){
									_plugins[i].OnSceneGUI(this, fpd, data);
								}
							}
						}
						#endregion
					}

					// Destroy temporary mesh collider if required
					if (newCollider) {
						DestroyImmediate(mc);
					}
				}
			}
		}
		#endregion
	}
}