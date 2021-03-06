﻿namespace MBrace.AWS.Runtime

open System
open System.Collections.Concurrent
open System.Runtime.Serialization

open Amazon
open Amazon.Util
open Amazon.Runtime
open Amazon.S3
open Amazon.SQS
open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.DataModel

open FSharp.AWS.DynamoDB

open MBrace.Runtime.Utils
open MBrace.AWS

[<AutoSerializable(false); NoEquality; NoComparison>]
type private AWSAccountData = 
    {
        Region          : RegionEndpoint
        Credentials     : MBraceAWSCredentials
        S3Client        : AmazonS3Client
        DynamoDBClient  : AmazonDynamoDBClient
        SQSClient       : AmazonSQSClient
    }

/// Defines a serializable AWS account descriptor which does not leak credentials
[<Sealed; DataContract; StructuredFormatDisplay("{StructuredFormatDisplay}")>]
type AWSAccount private (accountData : AWSAccountData) =
    static let localRegistry = new ConcurrentDictionary<string * string, AWSAccountData>()
    static let mkKey (accessKey : string) (region : RegionEndpoint) = accessKey, region.SystemName

    static let initAccountData (region : RegionEndpoint) (credentials : AWSCredentials) =
        {
            Region = region
            Credentials = MBraceAWSCredentials(credentials)
            S3Client = new AmazonS3Client(credentials, region)
            DynamoDBClient = new AmazonDynamoDBClient(credentials, region)
            SQSClient = new AmazonSQSClient(credentials, region)
        }

    static let recoverAccountData (accessKey : string) (region : RegionEndpoint) =
        let k = mkKey accessKey region
        let ok, found = localRegistry.TryGetValue k
        if ok then found 
        else
            let initFromProfileManager _ =
                let storedCreds =
                    ProfileManager.ListProfileNames() 
                    |> Seq.map ProfileManager.GetAWSCredentials
                    |> Seq.tryPick (fun creds -> if creds.GetCredentials().AccessKey = accessKey then Some creds else None)

                match storedCreds with
                | None -> invalidOp <| sprintf "Could not locate stored profile with access key '%s'" accessKey
                | Some credentials -> initAccountData region credentials

            localRegistry.GetOrAdd(k, initFromProfileManager)

    [<DataMember(Name = "AccessKey")>]
    let profileName = accountData.Credentials.AccessKey
    [<DataMember(Name = "RegionName")>]
    let regionName = accountData.Region.SystemName
    [<IgnoreDataMember>]
    let mutable localData = Some accountData

    let getAccountData() =
        match localData with
        | Some ld -> ld
        | None -> 
            let ld = recoverAccountData profileName (RegionEndpoint.GetBySystemName regionName)
            localData <- Some ld
            ld

    /// AWS account profile identifier
    member __.ProfileName = profileName
    member __.RegionName = regionName
    /// AWS account access Key
    member __.AccessKey = getAccountData().Credentials.AccessKey
    /// Credentials for AWS account
    member __.Credentials = getAccountData().Credentials
    /// Region endpoint identifier for account
    member __.Region = getAccountData().Region
    /// Amazon S3 Client instance for account
    member __.S3Client = getAccountData().S3Client :> IAmazonS3
    /// Amazon SQS Client instance for account
    member __.SQSClient = getAccountData().SQSClient :> IAmazonSQS
    /// Amazon DynamoDB Client instance for account
    member __.DynamoDBClient = getAccountData().DynamoDBClient :> IAmazonDynamoDB
    /// Gets table context using given name and record schema type
    member __.GetTableContext<'Schema>(tableName : string) =
        TableContext.Create<'Schema>(__.DynamoDBClient, tableName, verifyTable = false)

    interface IComparable with
        member __.CompareTo(other:obj) =
            match other with
            | :? AWSAccount as aa -> compare2 profileName regionName aa.ProfileName aa.RegionName
            | _ -> invalidArg "other" "invalid comparand."

    override __.Equals(other:obj) =
        match other with
        | :? AWSAccount as aa -> profileName = aa.ProfileName && regionName = aa.RegionName
        | _ -> false

    override __.GetHashCode() = hash2 profileName regionName

    member private __.StructuredFormatDisplay = sprintf "AWS Profile %s, Region %s" profileName regionName
    override __.ToString() = __.StructuredFormatDisplay

    /// <summary>
    ///     Creates a new AWS credentials with provided profile name and region endpoint.
    ///     Credentials will be recovered from the local profile manager.
    /// </summary>
    /// <param name="region">Region endpoint.</param>
    /// <param name="profileName">Profile name to recover credentials from,</param>
    static member Create(region : RegionEndpoint, credentials : AWSCredentials) =
        let initAccountData _ = initAccountData region credentials

        let accountData = localRegistry.GetOrAdd(mkKey (credentials.GetCredentials().AccessKey) region, initAccountData)
        new AWSAccount(accountData)

    /// <summary>
    ///     Creates a new AWS credentials with provided profile name and region endpoint.
    ///     Credentials will be recovered from the local profile manager.
    /// </summary>
    /// <param name="region">Region endpoint.</param>
    /// <param name="profileName">Profile name to recover credentials from,</param>
    static member Create(region : AWSRegion, credentials : AWSCredentials) =
        AWSAccount.Create(region.RegionEndpoint, credentials)