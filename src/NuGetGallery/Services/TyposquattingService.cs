﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;
using NuGet.Services.Entities;

namespace NuGetGallery
{
    public class TyposquattingService : ITyposquattingService
    {
        private static readonly IReadOnlyList<ThresholdInfo> ThresholdsList = new List<ThresholdInfo>
        {
            new ThresholdInfo (lowerBound: 0, upperBound: 30, threshold: 0),
            new ThresholdInfo (lowerBound: 30, upperBound: 50, threshold: 1),
            new ThresholdInfo (lowerBound: 50, upperBound: 129, threshold: 2)
        };

        private static int TyposquattingCheckListLength;

        private readonly IContentObjectService _contentObjectService;
        private readonly IPackageService _packageService;
        private readonly IReservedNamespaceService _reservedNamespaceService;
        private readonly ITelemetryService _telemetryService;

        public TyposquattingService(IContentObjectService contentObjectService, IPackageService packageService, IReservedNamespaceService reservedNamespaceService, ITelemetryService telemetryService)
        {
            _contentObjectService = contentObjectService ?? throw new ArgumentNullException(nameof(contentObjectService));
            _packageService = packageService ?? throw new ArgumentNullException(nameof(packageService));
            _reservedNamespaceService = reservedNamespaceService ?? throw new ArgumentNullException(nameof(reservedNamespaceService));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));

            TyposquattingCheckListLength = _contentObjectService.TyposquattingConfiguration.PackageIdChecklistLength;
        }

        public bool IsUploadedPackageIdTyposquatting(string uploadedPackageId, User uploadedPackageOwner, out List<string> typosquattingCheckCollisionIds)
        {
            typosquattingCheckCollisionIds = new List<string>();
            var wasUploadBlocked = false;
            if (!_contentObjectService.TyposquattingConfiguration.IsCheckEnabled || _reservedNamespaceService.GetReservedNamespacesForId(uploadedPackageId).Any())
            {
                return wasUploadBlocked;
            }

            if (uploadedPackageId == null)
            {
                throw new ArgumentNullException(nameof(uploadedPackageId));
            }

            if (uploadedPackageOwner == null)
            {
                throw new ArgumentNullException(nameof(uploadedPackageOwner));
            }

            var checklistRetrievalStopwatch = Stopwatch.StartNew();
            var packageRegistrations = _packageService.GetAllPackageRegistrations();
            var packagesCheckList = packageRegistrations
                .OrderByDescending(pr => pr.IsVerified)
                .ThenByDescending(pr => pr.DownloadCount)
                .Select(pr => pr.Id)
                .Take(TyposquattingCheckListLength)
                .ToList();
            checklistRetrievalStopwatch.Stop();

            var algorithmProcessingStopwatch = Stopwatch.StartNew();
            var threshold = GetThreshold(uploadedPackageId);
            var normalizedUploadedPackageId = TyposquattingStringNormalization.NormalizeString(uploadedPackageId);

            var collisionIds = new ConcurrentBag<string>();
            Parallel.ForEach(packagesCheckList, (packageId, loopState) =>
            {
                string normalizedPackageId = TyposquattingStringNormalization.NormalizeString(packageId);
                if (TyposquattingDistanceCalculation.IsDistanceLessThanThreshold(normalizedUploadedPackageId, normalizedPackageId, threshold))
                {
                    collisionIds.Add(packageId);
                }
            });

            algorithmProcessingStopwatch.Stop();

            var totalTime = checklistRetrievalStopwatch.Elapsed.Add(algorithmProcessingStopwatch.Elapsed);
            _telemetryService.TrackMetricForTyposquattingChecklistRetrievalTime(uploadedPackageId, checklistRetrievalStopwatch.Elapsed);
            _telemetryService.TrackMetricForTyposquattingAlgorithmProcessingTime(uploadedPackageId, algorithmProcessingStopwatch.Elapsed);

            if (collisionIds.Count == 0)
            {
                _telemetryService.TrackMetricForTyposquattingCheckResultAndTotalTime(
                    uploadedPackageId,
                    totalTime,
                    wasUploadBlocked,
                    typosquattingCheckCollisionIds,
                    TyposquattingCheckListLength);

                return false;
            }

            var ownersCheckStopwatch = Stopwatch.StartNew();
            var collisionPackagesIdAndOwners = packageRegistrations
                .Where(pr => collisionIds.Contains(pr.Id))
                .Select(pr => new { Id = pr.Id, Owners = pr.Owners.Select(x => x.Key).ToList() })
                .ToList();

            typosquattingCheckCollisionIds = collisionPackagesIdAndOwners
                .Where(pio => !pio.Owners.Any(k => k == uploadedPackageOwner.Key))
                .Select(pio => pio.Id)
                .ToList();

            /// <summary>
            /// The following statement is used to double check whether the collision Id belongs to the same user who is uploading the package.
            /// The current policy is that if the user has the ownership of any of the collision packages, we will pass the package.
            /// The reason is that maybe this user is trying to update an existing package who is owned by themselves.
            /// Example:
            /// User "a" is uploading a package named "xyz", while "xyz" collides with existing packages "xyzz" (owned by "a", "b", "c"), "xyyz" (owned by "b"), "xxyz" (owned by "b", "c").
            /// We will pass this package because "a" has the ownership of package "xyzz" even though this package Id collides with "xyyz" and "xxyz".
            /// The "typosquattingCheckCollisionIds" will be saved as "xyyz" and "xxyz" because this package collides with these two packages which are not owned by "a", while "xyzz" will not be saved as "a" owns it.
            /// </summary>
            var isUserAllowedTyposquatting = collisionPackagesIdAndOwners
                .Any(pio => pio.Owners.Any(k => k == uploadedPackageOwner.Key));

            wasUploadBlocked = _contentObjectService.TyposquattingConfiguration.IsBlockUsersEnabled && !isUserAllowedTyposquatting;

            ownersCheckStopwatch.Stop();

            totalTime = totalTime.Add(ownersCheckStopwatch.Elapsed);
            _telemetryService.TrackMetricForTyposquattingOwnersCheckTime(uploadedPackageId, ownersCheckStopwatch.Elapsed);
            _telemetryService.TrackMetricForTyposquattingCheckResultAndTotalTime(
                    uploadedPackageId,
                    totalTime,
                    wasUploadBlocked,
                    typosquattingCheckCollisionIds,
                    TyposquattingCheckListLength);

            return wasUploadBlocked;
        }

        private static int GetThreshold(string packageId)
        {
            foreach (var thresholdInfo in ThresholdsList)
            {
                if (packageId.Length >= thresholdInfo.LowerBound && packageId.Length < thresholdInfo.UpperBound)
                {
                    return thresholdInfo.Threshold;
                }
            }

            throw new ArgumentException(String.Format("There is no predefined typo-squatting threshold for this package Id: {0}", packageId));
        }
    }

    public class ThresholdInfo
    {
        public int LowerBound { get; }
        public int UpperBound { get; }
        public int Threshold { get; }
        public ThresholdInfo(int lowerBound, int upperBound, int threshold)
        {
            LowerBound = lowerBound;
            UpperBound = upperBound;
            Threshold = threshold;
        }        
    }
}