﻿using System;
using System.Collections.Generic;
using Svelto.DataStructures.Experimental;
using Svelto.ECS.Internal;

#if ENGINE_PROFILER_ENABLED && UNITY_EDITOR
using Svelto.ECS.Profiler;
#endif

namespace Svelto.ECS
{
    public partial class EnginesRoot : IDisposable
    {
        /// <summary>
        /// Dispose an EngineRoot once not used anymore, so that all the
        /// engines are notified with the entities removed.
        /// It's a clean up process.
        /// </summary>
        public void Dispose()
        {
            foreach (var groups in _groupEntityDB)
                foreach (var entityList in groups.Value)
                    entityList.Value.RemoveEntitiesFromEngines(_entityEngines);
        }

        ///--------------------------------------------

        public IEntityFactory GenerateEntityFactory()
        {
            return new GenericEntityFactory(new DataStructures.WeakReference<EnginesRoot>(this));
        }

        public IEntityFunctions GenerateEntityFunctions()
        {
            return new GenericEntityFunctions(new DataStructures.WeakReference<EnginesRoot>(this));
        }

        ///--------------------------------------------

        EntityStructInitializer BuildEntity<T>(EGID entityID, object[] implementors)
            where T : IEntityDescriptor, new()
        {
            var dic = EntityFactory.BuildGroupedEntityViews(entityID,
                              _groupedEntityToAdd.current,
                              EntityDescriptorTemplate<T>.descriptor.entitiesToBuild,
                              implementors);
            
            return new EntityStructInitializer(entityID, dic);
        }

        EntityStructInitializer BuildEntity(EGID entityID, 
                                IEntityBuilder[] entityToBuild,
                                object[] implementors)
        {
            var dic = EntityFactory.BuildGroupedEntityViews(entityID,
                                                  _groupedEntityToAdd.current,
                                                  entityToBuild,
                                                   implementors);
            
            return new EntityStructInitializer(entityID, dic);
        }

        ///--------------------------------------------

        void Preallocate<T>(int groupID, int size) where T : IEntityDescriptor, new()
        {
            var entityViewsToBuild = EntityDescriptorTemplate<T>.descriptor.entitiesToBuild;
            var count              = entityViewsToBuild.Length;

            //reserve space in the database
            Dictionary<Type, ITypeSafeDictionary> @group;
            if (_groupEntityDB.TryGetValue(groupID, out group) == false)
                group = _groupEntityDB[groupID] = new Dictionary<Type, ITypeSafeDictionary>();

            //reserve space in building buffer
            Dictionary<Type, ITypeSafeDictionary> @groupBuffer;
            if (_groupedEntityToAdd.current.TryGetValue(groupID, out @groupBuffer) == false)
                @groupBuffer = _groupedEntityToAdd.current[groupID] = new Dictionary<Type, ITypeSafeDictionary>();

            ITypeSafeDictionary dbList;

            for (var index = 0; index < count; index++)
            {
                var entityViewBuilder = entityViewsToBuild[index];
                var entityViewType    = entityViewBuilder.GetEntityType();

                if (group.TryGetValue(entityViewType, out dbList) == false)
                    group[entityViewType] = entityViewBuilder.Preallocate(ref dbList, size);
                else
                    dbList.AddCapacity(size);
                
                if (@groupBuffer.TryGetValue(entityViewType, out dbList) == false)
                    @groupBuffer[entityViewType] = entityViewBuilder.Preallocate(ref dbList, size);
                else
                    dbList.AddCapacity(size);
            }

            if (group.TryGetValue(_typeEntityInfoView, out dbList) == false)
                group[_typeEntityInfoView] = EntityBuilder<EntityInfoView>.Preallocate(ref dbList, size);
            else
                dbList.AddCapacity(size);

            if (@groupBuffer.TryGetValue(_typeEntityInfoView, out dbList) == false)
                @groupBuffer[_typeEntityInfoView] = EntityBuilder<EntityInfoView>.Preallocate(ref dbList, size);
            else
                dbList.AddCapacity(size);
        }
        
        ///--------------------------------------------
        /// 
        void MoveEntity(EGID entityGID, int toGroupID = -1, Dictionary<Type, ITypeSafeDictionary> toGroup = null)
        {
            var entityViewInfoDictionary = _groupEntityDB[entityGID.groupID][_typeEntityInfoView] as TypeSafeDictionary<EntityInfoView>;
            var entityBuilders = entityViewInfoDictionary[entityGID.entityID].entityToBuild;
            var entityBuildersCount = entityBuilders.Length;
           
            //for each entity view generated by the entity descriptor
            for (var i = 0; i < entityBuildersCount; i++)
            {
                var entityType = entityBuilders[i].GetEntityType();
                
                MoveEntity(entityGID, toGroupID, toGroup, entityType);
            }

            MoveEntity(entityGID, toGroupID, toGroup, _typeEntityInfoView);
        }

        void MoveEntity(EGID fromEntityGID, int toGroupID, Dictionary<Type, ITypeSafeDictionary> toGroup, Type entityType)
        {
            var fromGroup = _groupEntityDB[fromEntityGID.groupID];

            var fromTypeSafeDictionary = fromGroup[entityType];
            ITypeSafeDictionary safeDictionary = null;

            if (toGroup != null)
            {
                if (toGroup.TryGetValue(entityType, out safeDictionary) == false)
                {
                    safeDictionary = fromTypeSafeDictionary.Create();
                    toGroup.Add(entityType, safeDictionary);
                    _groupedGroups[entityType] = new FasterDictionary<int, ITypeSafeDictionary>();
                }

                _groupedGroups[entityType][toGroupID] = safeDictionary;
            }

            fromTypeSafeDictionary.MoveEntityFromDictionaryAndEngines(fromEntityGID, toGroupID, safeDictionary, _entityEngines);

            if (fromTypeSafeDictionary.Count == 0) //clean up
            {
                _groupedGroups[entityType].Remove(toGroupID);

                //it's probably better to not remove this, but the dictionary should be trimmed?
                //fromGroup.Remove(entityType);
                fromTypeSafeDictionary.Trim();
            }
            
            //it doesn't eliminate the fromGroup itself on purpose
        }

        void RemoveGroupAndEntitiesFromDB(int groupID)
        {
            foreach (var entiTypeSafeList in _groupEntityDB[groupID])
                entiTypeSafeList.Value.RemoveEntitiesFromEngines(_entityEngines);

            _groupEntityDB.Remove(groupID);
        }

        ///--------------------------------------------

        void SwapEntityGroup(int entityID, int fromGroupID, int toGroupID)
        {
            DBC.ECS.Check.Require(fromGroupID != toGroupID,
                          "can't move an entity to the same fromGroup where it already belongs to");

            Dictionary<Type, ITypeSafeDictionary> toGroup;

            if (_groupEntityDB.TryGetValue(toGroupID, out toGroup) == false)
                toGroup = _groupEntityDB[toGroupID] = new Dictionary<Type, ITypeSafeDictionary>();

            MoveEntity(new EGID(entityID, fromGroupID), toGroupID, toGroup);
        }
        
        EGID SwapFirstEntityGroup(int fromGroupID, int toGroupId)
        {
            var firstID =
                ((TypeSafeDictionary<EntityInfoView>) _groupEntityDB[fromGroupID][_typeEntityInfoView]).FasterValues[0].ID.entityID;
            
            SwapEntityGroup(firstID, fromGroupID, toGroupId);
            
            return new EGID(firstID, toGroupId);
        }

        readonly entitiesDB                                                _DB;
        readonly DoubleBufferedEntityViews<Dictionary<int, Dictionary<Type, ITypeSafeDictionary>>> _groupedEntityToAdd;
        
        static readonly Type                                               _typeEntityInfoView = typeof(EntityInfoView);
    }

    public struct EntityStructInitializer
    {
        public EntityStructInitializer(EGID id, Dictionary<Type, ITypeSafeDictionary> current)
        {
            _current = current;
            _id = id;
        }

        public void Init<T>(T initializer) where T: struct, IEntityStruct
        {
            var typeSafeDictionary = (TypeSafeDictionary<T>) _current[typeof(T)];

            initializer.ID = _id;

            int count;
            typeSafeDictionary.GetFasterValuesBuffer(out count)[typeSafeDictionary.FindElementIndex(_id.entityID)] = initializer;
        }

        readonly Dictionary<Type, ITypeSafeDictionary> _current;
        readonly EGID                                  _id;
    }
}