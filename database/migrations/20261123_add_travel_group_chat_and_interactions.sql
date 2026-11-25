/* Travel Group Chat & Interaction Hub Migration: Messages, Read Receipts, Roll Call, Split Bill Payments, Poll Votes */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'social.GroupMessages', N'U') IS NULL
BEGIN
    CREATE TABLE social.GroupMessages (
        message_id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_GroupMessages PRIMARY KEY,
        group_id BIGINT NOT NULL,
        sender_user_id BIGINT NOT NULL,
        message_type VARCHAR(20) NOT NULL,
        content NVARCHAR(MAX) NOT NULL,
        metadata_json NVARCHAR(MAX) NULL,
        created_at DATETIME2 NOT NULL
            CONSTRAINT DF_GroupMessages_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_GroupMessages_Type
            CHECK (message_type IN ('text', 'broadcast', 'roll_call', 'split_bill', 'poll')),
        CONSTRAINT FK_GroupMessages_Group
            FOREIGN KEY (group_id) REFERENCES social.TravelGroups(group_id) ON DELETE CASCADE,
        CONSTRAINT FK_GroupMessages_Sender
            FOREIGN KEY (sender_user_id) REFERENCES dbo.Users(user_id)
    );

    CREATE NONCLUSTERED INDEX IX_GroupMessages_GroupId_CreatedAt
        ON social.GroupMessages (group_id, created_at DESC)
        INCLUDE (sender_user_id, message_type);
END;

IF OBJECT_ID(N'social.GroupMessageReadReceipts', N'U') IS NULL
BEGIN
    CREATE TABLE social.GroupMessageReadReceipts (
        receipt_id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_GroupMessageReadReceipts PRIMARY KEY,
        message_id BIGINT NOT NULL,
        user_id BIGINT NOT NULL,
        read_at DATETIME2 NOT NULL
            CONSTRAINT DF_GroupMessageReadReceipts_ReadAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_GroupMessageReadReceipts_MessageUser
            UNIQUE (message_id, user_id),
        CONSTRAINT FK_GroupMessageReadReceipts_Message
            FOREIGN KEY (message_id) REFERENCES social.GroupMessages(message_id) ON DELETE CASCADE,
        CONSTRAINT FK_GroupMessageReadReceipts_User
            FOREIGN KEY (user_id) REFERENCES dbo.Users(user_id)
    );
END;

IF OBJECT_ID(N'social.GroupRollCallCheckins', N'U') IS NULL
BEGIN
    CREATE TABLE social.GroupRollCallCheckins (
        checkin_id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_GroupRollCallCheckins PRIMARY KEY,
        message_id BIGINT NOT NULL,
        user_id BIGINT NOT NULL,
        checked_in_at DATETIME2 NOT NULL
            CONSTRAINT DF_GroupRollCallCheckins_CheckedInAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_GroupRollCallCheckins_MessageUser
            UNIQUE (message_id, user_id),
        CONSTRAINT FK_GroupRollCallCheckins_Message
            FOREIGN KEY (message_id) REFERENCES social.GroupMessages(message_id) ON DELETE CASCADE,
        CONSTRAINT FK_GroupRollCallCheckins_User
            FOREIGN KEY (user_id) REFERENCES dbo.Users(user_id)
    );
END;

IF OBJECT_ID(N'social.GroupSplitBillPayments', N'U') IS NULL
BEGIN
    CREATE TABLE social.GroupSplitBillPayments (
        payment_id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_GroupSplitBillPayments PRIMARY KEY,
        message_id BIGINT NOT NULL,
        user_id BIGINT NOT NULL,
        is_paid BIT NOT NULL
            CONSTRAINT DF_GroupSplitBillPayments_IsPaid DEFAULT (0),
        paid_at DATETIME2 NULL,
        CONSTRAINT UQ_GroupSplitBillPayments_MessageUser
            UNIQUE (message_id, user_id),
        CONSTRAINT FK_GroupSplitBillPayments_Message
            FOREIGN KEY (message_id) REFERENCES social.GroupMessages(message_id) ON DELETE CASCADE,
        CONSTRAINT FK_GroupSplitBillPayments_User
            FOREIGN KEY (user_id) REFERENCES dbo.Users(user_id)
    );
END;

IF OBJECT_ID(N'social.GroupPollVotes', N'U') IS NULL
BEGIN
    CREATE TABLE social.GroupPollVotes (
        vote_id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_GroupPollVotes PRIMARY KEY,
        message_id BIGINT NOT NULL,
        option_index INT NOT NULL,
        user_id BIGINT NOT NULL,
        voted_at DATETIME2 NOT NULL
            CONSTRAINT DF_GroupPollVotes_VotedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_GroupPollVotes_OptionIndex
            CHECK (option_index >= 0 AND option_index <= 5),
        CONSTRAINT UQ_GroupPollVotes_MessageUser
            UNIQUE (message_id, user_id),
        CONSTRAINT FK_GroupPollVotes_Message
            FOREIGN KEY (message_id) REFERENCES social.GroupMessages(message_id) ON DELETE CASCADE,
        CONSTRAINT FK_GroupPollVotes_User
            FOREIGN KEY (user_id) REFERENCES dbo.Users(user_id)
    );
END;

COMMIT TRANSACTION;
