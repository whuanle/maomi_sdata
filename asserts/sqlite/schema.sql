CREATE TABLE "dashboard" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_dashboard" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "Kind" TEXT NOT NULL,
    "Description" TEXT NOT NULL,
    "ConfigJson" TEXT NOT NULL,
    "CreateUserId" INTEGER NOT NULL,
    "CreateTime" TEXT NOT NULL,
    "UpdateUserId" INTEGER NOT NULL,
    "UpdateTime" TEXT NOT NULL,
    "IsDeleted" INTEGER NOT NULL
);


CREATE TABLE "device" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_device" PRIMARY KEY AUTOINCREMENT,
    "DeviceKey" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Group" TEXT NOT NULL,
    "TagsJson" TEXT NOT NULL,
    "Enabled" INTEGER NOT NULL,
    "LastSeenTime" TEXT NULL,
    "CreateUserId" INTEGER NOT NULL,
    "CreateTime" TEXT NOT NULL,
    "UpdateUserId" INTEGER NOT NULL,
    "UpdateTime" TEXT NOT NULL,
    "IsDeleted" INTEGER NOT NULL
);


CREATE TABLE "ingest_key" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ingest_key" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "KeyHash" TEXT NOT NULL,
    "Enabled" INTEGER NOT NULL,
    "CreateUserId" INTEGER NOT NULL,
    "CreateTime" TEXT NOT NULL,
    "UpdateUserId" INTEGER NOT NULL,
    "UpdateTime" TEXT NOT NULL,
    "IsDeleted" INTEGER NOT NULL
);


CREATE TABLE "saved_query" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_saved_query" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "SqlText" TEXT NOT NULL,
    "Description" TEXT NOT NULL,
    "CreateUserId" INTEGER NOT NULL,
    "CreateTime" TEXT NOT NULL,
    "UpdateUserId" INTEGER NOT NULL,
    "UpdateTime" TEXT NOT NULL,
    "IsDeleted" INTEGER NOT NULL
);


CREATE TABLE "setting" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_setting" PRIMARY KEY AUTOINCREMENT,
    "Key" TEXT NOT NULL,
    "Value" TEXT NOT NULL
);


CREATE TABLE "user" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_user" PRIMARY KEY AUTOINCREMENT,
    "Username" TEXT NOT NULL,
    "PasswordHash" TEXT NOT NULL,
    "Role" TEXT NOT NULL,
    "CreateUserId" INTEGER NOT NULL,
    "CreateTime" TEXT NOT NULL,
    "UpdateUserId" INTEGER NOT NULL,
    "UpdateTime" TEXT NOT NULL,
    "IsDeleted" INTEGER NOT NULL
);


CREATE UNIQUE INDEX "IX_device_DeviceKey" ON "device" ("DeviceKey");


CREATE UNIQUE INDEX "IX_ingest_key_KeyHash" ON "ingest_key" ("KeyHash");


CREATE UNIQUE INDEX "IX_setting_Key" ON "setting" ("Key");


CREATE UNIQUE INDEX "IX_user_Username" ON "user" ("Username");


