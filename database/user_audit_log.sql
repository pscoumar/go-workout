CREATE TABLE IF NOT EXISTS `user_audit_log` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `user_id` varchar(75) NOT NULL,
  `action_type` enum('ADD','RENEW','UPDATE','SUSPEND','DELETE') NOT NULL,
  `old_expiry` date DEFAULT NULL,
  `new_expiry` date DEFAULT NULL,
  `old_data` json DEFAULT NULL,
  `new_data` json DEFAULT NULL,
  `performed_by` varchar(50) DEFAULT NULL,
  `created_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`),
  KEY `idx_user_date` (`user_id`,`created_at`),
  KEY `idx_action_date` (`action_type`,`created_at`),
  KEY `idx_performed_by` (`performed_by`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
