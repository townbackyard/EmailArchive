namespace EmailArchive.Core.Model;

public enum KeyType { MessageId, Fallback }

public enum MessageState { Pending, AlreadyInTarget, Copied, Verified, Trashed, Purged, Failed }

public enum TargetFolder { Archive, Sent }

public enum RunMode { Report, Reconcile, Move, Dedupe }
